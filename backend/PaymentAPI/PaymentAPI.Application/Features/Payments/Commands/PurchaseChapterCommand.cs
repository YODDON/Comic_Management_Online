using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using MediatR;
using MassTransit;
using SharedKernel.Responses;
using SharedKernel.Enums;
using PaymentAPI.DTOs;
using PaymentAPI.Entities;
using PaymentAPI.Interfaces;
using PaymentAPI.Settings;
using SharedKernel.Contracts.Modules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedKernel.Events;
using SharedKernel.Contracts.Purchase;

using PaymentAPI.Services;
namespace PaymentAPI.Application.Features.Payments.Commands
{
    public class PurchaseChapterCommand : IRequest<ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>>
    {
        public int UserId { get; set; }
        public Guid ChapterId { get; set; }
    }

    public class PurchaseChapterCommandHandler : IRequestHandler<PurchaseChapterCommand, ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>>
    {
        private const string VietQrImageBaseUrl = "https://img.vietqr.io/image";
        // T45: the lock is held only for the check, the insert and the commit, so 10 s is far above any normal wait.
        private const int PurchaseLockTimeoutMs = 10000;
        private readonly IPaymentRepository _repository;
        private readonly IPaymentChapterGateway _chapterGateway;
        private readonly IPaymentWalletGateway _walletGateway;
        private readonly IRequestClient<SubmitPurchaseCommand> _purchaseClient;
        private readonly TopUpSettings _topUpSettings;
        private readonly BankSettings _bankSettings;

        public PurchaseChapterCommandHandler(IPaymentRepository repository, IPaymentChapterGateway chapterGateway, IPaymentWalletGateway walletGateway, IRequestClient<SubmitPurchaseCommand> purchaseClient, IOptions<TopUpSettings> topUpOptions, IOptions<BankSettings> bankOptions)
        {
            _repository = repository;
            _chapterGateway = chapterGateway;
            _walletGateway = walletGateway;
            _purchaseClient = purchaseClient;
            _topUpSettings = topUpOptions.Value;
            _bankSettings = bankOptions.Value;
        }

        public async Task<ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>> Handle(PurchaseChapterCommand request, CancellationToken cancellationToken)
        {
            var existingPurchase = await _repository.GetUserPurchaseAsync(request.UserId, request.ChapterId);
            if (existingPurchase != null)
            {
                return await AlreadyPurchasedAsync(request, existingPurchase.Price);
            }

            ChapterInfo chapterInfo;
            try
            {
                chapterInfo = await _chapterGateway.GetChapterInfoAsync(request.ChapterId);
            }
            catch (ModuleCallException)
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Không thể lấy thông tin chapter. Vui lòng thử lại sau.", 503);
            }

            if (!chapterInfo.Exists)
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Không tìm thấy chapter.", 404);
            }

            if (!string.Equals(chapterInfo.Status, "Published", StringComparison.OrdinalIgnoreCase))
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Chapter chưa được phát hành.", 404);
            }

            decimal price = chapterInfo.UnitPrice;
            if (price <= 0)
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Chapter miễn phí không cần mở khóa.", 400);
            }

            await _repository.BeginTransactionAsync();

            // T45: one open purchase per user and chapter. The application lock is held until the Pending row below is
            // committed (or this transaction rolls back), so a second request for the same user and chapter waits here
            // and then sees that committed row instead of starting a second debit.
            Transaction? openPurchase;
            try
            {
                if (!await _repository.AcquirePurchaseLockAsync(request.UserId, request.ChapterId, PurchaseLockTimeoutMs))
                {
                    await _repository.RollbackTransactionAsync();
                    return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                        "Hệ thống đang bận. Vui lòng thử lại.", 503);
                }

                openPurchase = await _repository.GetOpenPurchaseAsync(request.UserId, request.ChapterId);
            }
            catch
            {
                await _repository.RollbackTransactionAsync();
                throw;
            }

            if (openPurchase != null)
            {
                await _repository.RollbackTransactionAsync();
                if (openPurchase.Status == TransactionStatus.Completed)
                {
                    return await AlreadyPurchasedAsync(request, openPurchase.Amount);
                }

                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Chapter này đang được thanh toán. Vui lòng chờ giao dịch trước hoàn tất.", 409);
            }

            var walletDebited = false;
            decimal? remainingBalance = null;
            var transaction = new Transaction
            {
                UserId = request.UserId,
                ChapterId = request.ChapterId,
                Type = TransactionType.Purchase,
                Amount = price,
                CurrencyType = "Dâu",
                Status = TransactionStatus.Pending
            };

            try
            {
                await _repository.AddTransactionAsync(transaction);
                await _repository.CommitTransactionAsync();

                // Fire the Saga
                var response = await _purchaseClient.GetResponse<PurchaseCompletedEvent, PurchaseFailedEvent>(
                    new SubmitPurchaseCommand(
                        CorrelationId: Guid.NewGuid(),
                        UserId: request.UserId,
                        ComicId: chapterInfo.ComicId,
                        ChapterId: request.ChapterId,
                        Price: price,
                        TransactionId: transaction.Id
                    ));

                if (response.Is(out Response<PurchaseCompletedEvent> completed))
                {
                    return new ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>(
                        new PaymentAPI.DTOs.ChapterPurchaseResultDto
                        {
                            ChapterId = request.ChapterId,
                            Price = price,
                            // The saga does not know the balance and sends -1: answer "unknown" (null), never a negative balance.
                            RemainingBalance = completed.Message.RemainingBalance < 0 ? null : completed.Message.RemainingBalance,
                            AlreadyPurchased = false
                        },
                        "Mở khóa chapter thành công.");
                }
                else if (response.Is(out Response<PurchaseFailedEvent> failed))
                {
                    return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                        $"Mở khóa chapter thất bại: {failed.Message.Reason}", 402);
                }
                
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Trạng thái giao dịch không xác định.", 500);
            }
            catch (RequestTimeoutException)
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Giao dịch đang được xử lý ngầm. Vui lòng kiểm tra lại sau.", 202);
            }
            catch (Exception)
            {
                // Unhandled exception means the command might not have been sent.
                transaction.Status = TransactionStatus.Failed;
                transaction.Note = "Lỗi hệ thống khi khởi tạo giao dịch mua.";
                await _repository.SaveFailedTransactionAsync(transaction);
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "Đã xảy ra lỗi khi thanh toán. Vui lòng thử lại.", 500);
            }
        }

        // The "already unlocked" answer, moved unchanged from the first guard of Handle (T45 also uses it when the
        // locked check finds a Completed purchase).
        private async Task<ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>> AlreadyPurchasedAsync(PurchaseChapterCommand request, decimal price)
        {
            // Repair a partial cross-service purchase if PaymentDB committed previously but the
            // corresponding ChapterDB unlock did not.
            try
            {
                var existingUnlock = await _chapterGateway.UnlockChapterAsync(request.UserId, request.ChapterId);
                if (!existingUnlock.Success)
                {
                    return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                        "Không thể khôi phục quyền đọc chapter đã mua.", 502);
                }
            }
            catch (ModuleCallException)
            {
                return ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>.ErrorResponse(
                    "ChapterAPI đang tạm thời không khả dụng.", 503);
            }

            return new ApiResponse<PaymentAPI.DTOs.ChapterPurchaseResultDto>(
                new PaymentAPI.DTOs.ChapterPurchaseResultDto
                {
                    ChapterId = request.ChapterId,
                    Price = price,
                    AlreadyPurchased = true
                },
                "Chapter này đã được mở khóa trước đó.");
        }
private string BuildVietQrUrl(decimal amount, string transactionCode)
        {
            // The amount must be a plain integer: "10000", never "10,000.00". Without InvariantCulture
            // a server in a comma-decimal locale would emit an amount the banking app cannot read.
            var amountText = ((long)amount).ToString(CultureInfo.InvariantCulture);

            var url = $"{VietQrImageBaseUrl}/{_bankSettings.BankCode}-{_bankSettings.AccountNumber}-compact2.png"
                    + $"?amount={amountText}"
                    + $"&addInfo={Uri.EscapeDataString(transactionCode)}";

            if (!string.IsNullOrWhiteSpace(_bankSettings.AccountName))
            {
                url += $"&accountName={Uri.EscapeDataString(_bankSettings.AccountName)}";
            }

            return url;
        }

private static string BuildSePayNote(PaymentAPI.DTOs.SePayWebhookDto request, decimal requestedAmount)
        {
            var note = $"SEPAY id={request.Id}; ref={request.ReferenceCode}; "
                     + $"gateway={request.Gateway}; date={request.TransactionDate}; "
                     + $"received={request.TransferAmount}";

            // Flag the mismatch so an over/underpayment is visible on the transaction record.
            if (requestedAmount != request.TransferAmount)
            {
                note += $"; requested={requestedAmount} (MISMATCH)";
            }

            // Note is nvarchar(1000).
            return note.Length > 1000 ? note[..1000] : note;
        }

private async Task<bool> CreditWalletForTopUpAsync(Transaction transaction)
        {
            var coins = transaction.Amount * _topUpSettings.CoinRate;
            if (coins <= 0)
            {
                return false;
            }

            try
            {
                var response = await _walletGateway.AddCoinAsync(
                    transaction.UserId,
                    coins,
                    transaction.Id,
                    $"Top-up {transaction.TransactionCode}",
                    // Real money in — the only kind of credit that may later be withdrawn as cash.
                    SharedKernel.Enums.TransactionType.ManualTopUp.ToString());

                return response.Success;
            }
            catch (ModuleCallException)
            {
                // Wallet unreachable: leave the transaction Pending so it can be retried,
                // rather than completing it against a balance that never changed.
                return false;
            }
        }

private static Guid CreateRefundReference(Guid transactionId)
        {
            var bytes = transactionId.ToByteArray();
            bytes[15] ^= 0xFF;
            return new Guid(bytes);
        }

private async Task EnrichChapterTransactionsAsync(
            IEnumerable<PaymentAPI.DTOs.TransactionDto> transactions)
        {
            var purchaseTransactions = transactions
                .Where(transaction => transaction.Type == TransactionType.Purchase
                    && transaction.ChapterId.HasValue)
                .ToList();

            await Task.WhenAll(purchaseTransactions.Select(async transaction =>
            {
                try
                {
                    var chapter = await _chapterGateway.GetChapterInfoAsync(transaction.ChapterId!.Value);
                    if (!chapter.Exists) return;

                    transaction.ComicId = chapter.ComicId;
                    transaction.ChapterTitle = chapter.Title;
                    transaction.ChapterNumber = chapter.ChapterNumber;
                    transaction.ChapterSlug = chapter.Slug;
                }
                catch (ModuleCallException)
                {
                    // History remains available with ChapterId even if ChapterAPI is temporarily down.
                }
            }));
        }
    }
}
