using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Interfaces;
using PaymentAPI.Settings;
using SharedKernel.Contracts.Modules;
using SharedKernel.Contracts.Purchase;
using SharedKernel.Enums;

namespace PaymentAPI.Application.Services
{
    /// <summary>What one run of <see cref="PurchaseRecoveryJob"/> did, per outcome.</summary>
    public sealed record PurchaseRecoveryResult(
        int Completed, int Failed, int Refunded, int SagasClosed, int Critical, int Skipped, int Unchanged, int Errors);

    /// <summary>
    /// Settles chapter purchases left stuck by a crash or restart (T23). The host bus is in-memory, so the saga's
    /// messages in flight are lost; the transaction stays Pending and the saga row stays in its last state.
    /// <para>
    /// For each candidate the job reads three facts, never the saga's price or a recomputed value:
    /// D = the wallet debit entry (<c>ReferenceId == TransactionId</c>), R = the refund entry
    /// (<see cref="PurchaseReferenceIds.RefundOf"/>), U = the chapter unlock row. Then, for a Pending transaction:
    /// not D: Failed, no refund. D and U: Completed (Critical when the comic id is unknown). D and not U: refund the
    /// debit entry's amount, then Failed. D and R and not U: Failed. Anything else is Critical: logged, left as it is.
    /// </para>
    /// <para>
    /// Safe to run twice or concurrently: every change runs in one Payment database transaction that locks the saga row
    /// and the transaction row and re-checks that neither changed since the facts were read. The refund runs inside that
    /// section, after re-checking that the chapter is still not unlocked and not yet refunded; it is idempotent by its
    /// reference id, so a commit that fails after the refund only leaves "refunded, mark Failed" for the next run. A purchase whose transaction or last saga step is newer than
    /// <see cref="PurchaseRecoverySettings.StuckAfterMinutes"/> is never touched.
    /// </para>
    /// The Completed and Failed side effects are the consumers' own (<see cref="PurchaseOutcomes"/>).
    /// </summary>
    public sealed class PurchaseRecoveryJob
    {
        /// <summary>Start of the note of every transaction this job marks Failed.</summary>
        public const string NotePrefix = "[recovery]";

        private const int BatchSize = 100;

        /// <summary>Failed-by-the-job purchases are re-checked this long for a debit that arrived afterwards.</summary>
        private static readonly TimeSpan LateDebitWindow = TimeSpan.FromHours(24);

        /// <summary>
        /// Saga states that still wait for a message. A finalized (completed) saga row is deleted by the saga
        /// (<c>SetCompletedWhenFinalized</c>); a "Failed" row with a Pending transaction is found through the
        /// Pending-transaction query.
        /// </summary>
        private static readonly string[] OpenSagaStates = { "Initial", "ProcessingPayment", "UnlockingChapter", "RefundingWallet" };

        private const string FailedSagaState = "Failed";

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IOptions<PurchaseRecoverySettings> _options;
        private readonly ILogger<PurchaseRecoveryJob> _logger;

        public PurchaseRecoveryJob(
            IServiceScopeFactory scopeFactory,
            IOptions<PurchaseRecoverySettings> options,
            ILogger<PurchaseRecoveryJob> logger)
        {
            _scopeFactory = scopeFactory;
            _options = options;
            _logger = logger;
        }

        private enum Outcome { Completed, Failed, Refunded, SagaClosed, Critical, Skipped, Unchanged }

        public async Task<PurchaseRecoveryResult> RunOnceAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var cutoff = now.AddMinutes(-Math.Max(0, _options.Value.StuckAfterMinutes));

            var candidates = new List<Guid>();
            using (var scope = _scopeFactory.CreateScope())
            {
                var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();
                candidates.AddRange(await repository.GetPendingPurchaseIdsAsync(cutoff, BatchSize));
                candidates.AddRange(await repository.GetSagaTransactionIdsAsync(OpenSagaStates, cutoff, BatchSize));
                candidates.AddRange(await repository.GetFailedPurchaseIdsByNotePrefixAsync(NotePrefix, now - LateDebitWindow, BatchSize));
            }

            var counts = new Dictionary<Outcome, int>();
            var errors = 0;
            foreach (var transactionId in candidates.Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var outcome = await RecoverAsync(transactionId, cutoff, cancellationToken);
                    counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    errors++;
                    _logger.LogError(ex,
                        "Purchase recovery: transaction {TransactionId} could not be settled; it is retried on the next run.",
                        transactionId);
                }
            }

            var result = new PurchaseRecoveryResult(
                counts.GetValueOrDefault(Outcome.Completed),
                counts.GetValueOrDefault(Outcome.Failed),
                counts.GetValueOrDefault(Outcome.Refunded),
                counts.GetValueOrDefault(Outcome.SagaClosed),
                counts.GetValueOrDefault(Outcome.Critical),
                counts.GetValueOrDefault(Outcome.Skipped),
                counts.GetValueOrDefault(Outcome.Unchanged),
                errors);

            if (result != new PurchaseRecoveryResult(0, 0, 0, 0, 0, 0, result.Unchanged, 0))
            {
                _logger.LogInformation("Purchase recovery run finished: {Result}", result);
            }

            return result;
        }

        private async Task<Outcome> RecoverAsync(Guid transactionId, DateTime cutoff, CancellationToken cancellationToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var services = scope.ServiceProvider;
            var repository = services.GetRequiredService<IPaymentRepository>();
            var wallet = services.GetRequiredService<IPaymentWalletGateway>();
            var chapter = services.GetRequiredService<IPaymentChapterGateway>();

            // Snapshot. The Payment changes later run in a fresh scope, so these tracked values never hide a newer row.
            var transaction = await repository.GetTransactionByIdAsync(transactionId);
            var saga = await repository.GetPurchaseStateAsync(transactionId);
            if (transaction == null)
            {
                return Skipped(transactionId, "transaction not found");
            }

            var facts = new Facts(transaction.UserId, transaction.ChapterId, transaction.Status, saga?.CurrentState, saga?.CorrelationId);

            if (transaction.Type != TransactionType.Purchase || transaction.ChapterId == null)
            {
                return Critical(transactionId, facts, "not a chapter purchase");
            }

            // Only a saga that still waits for a message can be "still running"; a Failed row's UpdatedAt may be the
            // time this job closed it.
            if (saga != null && OpenSagaStates.Contains(saga.CurrentState) && (saga.UpdatedAt ?? saga.CreatedAt) >= cutoff)
            {
                return Skipped(transactionId, "saga still active");
            }

            if (transaction.Status == TransactionStatus.Pending && transaction.CreatedAt >= cutoff)
            {
                return Skipped(transactionId, "transaction too recent");
            }

            WalletLedgerEntry? debit;
            WalletLedgerEntry? refund;
            bool unlocked;
            try
            {
                debit = await wallet.GetLedgerEntryAsync(transactionId, cancellationToken);
                refund = await wallet.GetLedgerEntryAsync(PurchaseReferenceIds.RefundOf(transactionId), cancellationToken);
                unlocked = await chapter.IsChapterUnlockedAsync(transaction.UserId, transaction.ChapterId.Value, cancellationToken);
            }
            catch (ModuleCallException ex)
            {
                return Critical(transactionId, facts, $"cannot read the wallet ledger or the chapter unlock: {ex.Message}");
            }

            facts = facts with { Debited = debit != null, Refunded = refund != null, Unlocked = unlocked };

            if (debit != null && (debit.UserId != transaction.UserId || debit.Type != nameof(TransactionType.Purchase) || debit.Amount >= 0))
            {
                return Critical(transactionId, facts, $"unexpected debit entry (user {debit.UserId}, type {debit.Type}, amount {debit.Amount})");
            }

            if (refund != null && (refund.UserId != transaction.UserId || refund.Type != nameof(TransactionType.Refund)
                                   || debit == null || refund.Amount != -debit.Amount))
            {
                return Critical(transactionId, facts, $"unexpected refund entry (user {refund.UserId}, type {refund.Type}, amount {refund.Amount})");
            }

            switch (transaction.Status)
            {
                case TransactionStatus.Pending:
                    return await SettlePendingAsync(transactionId, saga, facts, debit, refund, unlocked, wallet, chapter, cancellationToken);

                case TransactionStatus.Failed:
                    // Failed earlier (by the saga, the purchase handler or this job). Money must not stay debited
                    // without an unlock: refund a debit that has no refund and no unlock.
                    if (debit != null && refund == null && unlocked)
                    {
                        return Critical(transactionId, facts, "transaction Failed but the user was charged and the chapter is unlocked");
                    }

                    if (debit != null && refund == null)
                    {
                        var lateRefund = await RefundAndFailAsync(
                            transactionId, saga, TransactionStatus.Failed, facts, debit, reason: null, wallet, chapter, cancellationToken);
                        return lateRefund switch
                        {
                            RefundResult.Refunded => Logged(Outcome.Refunded, transactionId, facts, $"late debit refunded ({-debit.Amount}); transaction stays Failed"),
                            RefundResult.AlreadyRefunded => Logged(Outcome.SagaClosed, transactionId, facts, "late debit already refunded; transaction stays Failed"),
                            _ => Skipped(transactionId, "changed while being settled (unlocked, or saga or transaction moved); not refunded"),
                        };
                    }

                    if (saga != null && saga.CurrentState != FailedSagaState
                        && await CloseSagaAsFailedAsync(transactionId, saga, TransactionStatus.Failed, null, wallet, cancellationToken))
                    {
                        return Logged(Outcome.SagaClosed, transactionId, facts, "saga row set to Failed (transaction already Failed)");
                    }

                    return Unchanged(transactionId, facts);

                case TransactionStatus.Completed:
                    if (debit == null || refund != null || !unlocked)
                    {
                        return Critical(transactionId, facts, "transaction Completed but the ledger or the unlock does not match");
                    }

                    if (saga != null && await ApplyAsync(transactionId, saga, TransactionStatus.Completed, async (repo, locked) =>
                        {
                            if (locked != null) await repo.RemovePurchaseStateAsync(locked);
                            return true;
                        }))
                    {
                        return Logged(Outcome.SagaClosed, transactionId, facts, "saga row removed (transaction already Completed)");
                    }

                    return Unchanged(transactionId, facts);

                default:
                    return Critical(transactionId, facts, $"unexpected transaction status {transaction.Status}");
            }
        }

        private async Task<Outcome> SettlePendingAsync(
            Guid transactionId, PurchaseState? saga, Facts facts,
            WalletLedgerEntry? debit, WalletLedgerEntry? refund, bool unlocked,
            IPaymentWalletGateway wallet, IPaymentChapterGateway chapter, CancellationToken cancellationToken)
        {
            if (debit == null)
            {
                // A refund without a debit was already rejected as Critical above.
                var reason = $"{NotePrefix} {saga?.ErrorReason ?? "Giao dịch bị gián đoạn, chưa trừ tiền."}";
                return await CloseSagaAsFailedAsync(transactionId, saga, TransactionStatus.Pending, reason, wallet, cancellationToken)
                    ? Logged(Outcome.Failed, transactionId, facts, "not debited: Failed, no refund")
                    : Skipped(transactionId, "changed while being settled (debited, or saga or transaction moved)");
            }

            if (refund == null && unlocked)
            {
                var comicId = saga?.ComicId ?? Guid.Empty;
                if (comicId == Guid.Empty)
                {
                    var info = await chapter.GetChapterInfoAsync(facts.ChapterId!.Value, cancellationToken);
                    comicId = info.Exists ? info.ComicId : Guid.Empty;
                }

                if (comicId == Guid.Empty)
                {
                    // Never record a Payment UserPurchase with an empty ComicId.
                    return Critical(transactionId, facts, "debited and unlocked, but the comic id is unknown (no saga value, no chapter info)");
                }

                var completed = await ApplyAsync(transactionId, saga, TransactionStatus.Pending, async (repo, locked) =>
                {
                    await PurchaseOutcomes.RecordCompletedAsync(repo, transactionId, comicId);
                    if (locked != null) await repo.RemovePurchaseStateAsync(locked);
                    return true;
                });
                return completed
                    ? Logged(Outcome.Completed, transactionId, facts, "debited and unlocked: Completed")
                    : Skipped(transactionId, "changed while being settled");
            }

            if (unlocked)
            {
                return Critical(transactionId, facts, "debited, refunded and unlocked");
            }

            var result = await RefundAndFailAsync(
                transactionId, saga, TransactionStatus.Pending, facts, debit,
                $"{NotePrefix} Giao dịch bị gián đoạn, chapter chưa mở khóa, đã hoàn tiền.", wallet, chapter, cancellationToken);
            return result switch
            {
                RefundResult.Refunded => Logged(Outcome.Refunded, transactionId, facts, $"debited, not unlocked: refunded {-debit.Amount}, Failed"),
                RefundResult.AlreadyRefunded => Logged(Outcome.Failed, transactionId, facts, "already refunded: Failed"),
                _ => Skipped(transactionId, "changed while being settled (unlocked, or saga or transaction moved); not refunded"),
            };
        }

        private enum RefundResult { Refunded, AlreadyRefunded, Aborted }

        /// <summary>
        /// Refund path, all inside the Payment locks (<see cref="ApplyAsync"/>): re-check that the chapter is still not
        /// unlocked, refund the debit entry's amount unless a refund entry exists, mark the transaction Failed (when
        /// <paramref name="reason"/> is set and it is still Pending) and the saga row Failed, commit. If the commit fails
        /// after the refund, the next run sees the refund entry and only marks the purchase Failed.
        /// </summary>
        private async Task<RefundResult> RefundAndFailAsync(
            Guid transactionId, PurchaseState? saga, TransactionStatus expected, Facts facts, WalletLedgerEntry debit, string? reason,
            IPaymentWalletGateway wallet, IPaymentChapterGateway chapter, CancellationToken cancellationToken)
        {
            var result = RefundResult.Aborted;
            var committed = await ApplyAsync(transactionId, saga, expected, async (repo, locked) =>
            {
                // The saga may have unlocked the chapter after the facts were read: its saga row changes only when the
                // ChapterUnlockedEvent is processed, so the row check alone does not see it.
                if (await chapter.IsChapterUnlockedAsync(facts.UserId, facts.ChapterId!.Value, cancellationToken))
                {
                    return false;
                }

                if (await wallet.GetLedgerEntryAsync(PurchaseReferenceIds.RefundOf(transactionId), cancellationToken) == null)
                {
                    // The amount is the debit entry's, never the chapter price or the saga's price.
                    await wallet.RefundPurchaseAsync(facts.UserId, -debit.Amount, transactionId, cancellationToken);
                    result = RefundResult.Refunded;
                }
                else
                {
                    result = RefundResult.AlreadyRefunded;
                }

                await MarkFailedAsync(repo, locked, transactionId, reason);
                return true;
            });

            return committed ? result : RefundResult.Aborted;
        }

        /// <summary>
        /// Marks the transaction Failed with <paramref name="reason"/> (when set and the transaction is still Pending) and
        /// the saga row Failed. On the not-debited path (<paramref name="expected"/> Pending) it first re-checks under the
        /// Payment locks that there is still no debit. Returns false when anything changed.
        /// </summary>
        private Task<bool> CloseSagaAsFailedAsync(
            Guid transactionId, PurchaseState? saga, TransactionStatus expected, string? reason,
            IPaymentWalletGateway wallet, CancellationToken cancellationToken)
        {
            return ApplyAsync(transactionId, saga, expected, async (repo, locked) =>
            {
                if (expected == TransactionStatus.Pending
                    && await wallet.GetLedgerEntryAsync(transactionId, cancellationToken) != null)
                {
                    return false; // debited meanwhile: the next run settles it with the debit
                }

                await MarkFailedAsync(repo, locked, transactionId, reason);
                return true;
            });
        }

        private static async Task MarkFailedAsync(IPaymentRepository repo, PurchaseState? locked, Guid transactionId, string? reason)
        {
            if (reason != null)
            {
                await PurchaseOutcomes.RecordFailedAsync(repo, transactionId, reason);
            }

            if (locked != null && locked.CurrentState != FailedSagaState)
            {
                locked.CurrentState = FailedSagaState;
                locked.ErrorReason ??= reason ?? $"{NotePrefix} transaction already Failed";
                locked.UpdatedAt = DateTime.UtcNow;
                await repo.UpdatePurchaseStateAsync(locked);
            }
        }

        /// <summary>
        /// Runs <paramref name="apply"/> in one Payment database transaction after locking the saga row and the
        /// transaction row (saga first, the same order as a running saga step) and checking that the transaction still
        /// has <paramref name="expected"/> status and the saga row is unchanged since <paramref name="snapshot"/>.
        /// <paramref name="apply"/> returns false to abort (rollback). Returns whether the changes were committed.
        /// </summary>
        private async Task<bool> ApplyAsync(
            Guid transactionId, PurchaseState? snapshot, TransactionStatus expected,
            Func<IPaymentRepository, PurchaseState?, Task<bool>> apply)
        {
            using var scope = _scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IPaymentRepository>();

            await repository.BeginTransactionAsync();
            try
            {
                var saga = await repository.LockPurchaseStateAsync(transactionId);
                var transaction = await repository.LockTransactionAsync(transactionId);
                var sagaUnchanged = snapshot == null
                    ? saga == null
                    : saga != null && saga.CurrentState == snapshot.CurrentState && saga.UpdatedAt == snapshot.UpdatedAt;

                if (transaction == null || transaction.Status != expected || !sagaUnchanged
                    || !await apply(repository, saga))
                {
                    await repository.RollbackTransactionAsync();
                    return false;
                }

                await repository.CommitTransactionAsync();
                return true;
            }
            catch
            {
                await repository.RollbackTransactionAsync();
                throw;
            }
        }

        private sealed record Facts(int UserId, Guid? ChapterId, TransactionStatus Status, string? SagaState, Guid? CorrelationId)
        {
            public bool? Debited { get; init; }
            public bool? Refunded { get; init; }
            public bool? Unlocked { get; init; }
        }

        private Outcome Logged(Outcome outcome, Guid transactionId, Facts facts, string decision)
        {
            var level = outcome == Outcome.Refunded ? LogLevel.Warning : LogLevel.Information;
            Log(level, outcome, transactionId, facts, decision);
            return outcome;
        }

        private Outcome Critical(Guid transactionId, Facts facts, string decision)
        {
            Log(LogLevel.Critical, Outcome.Critical, transactionId, facts, decision + "; left as it is, needs manual review");
            return Outcome.Critical;
        }

        private Outcome Unchanged(Guid transactionId, Facts facts)
        {
            Log(LogLevel.Debug, Outcome.Unchanged, transactionId, facts, "consistent, nothing to do");
            return Outcome.Unchanged;
        }

        private Outcome Skipped(Guid transactionId, string decision)
        {
            _logger.LogInformation("Purchase recovery {Outcome}: transaction {TransactionId}, {Decision}.",
                Outcome.Skipped, transactionId, decision);
            return Outcome.Skipped;
        }

        private void Log(LogLevel level, Outcome outcome, Guid transactionId, Facts facts, string decision)
        {
            _logger.Log(level,
                "Purchase recovery {Outcome}: transaction {TransactionId}, user {UserId}, chapter {ChapterId}, status {Status}, "
                + "saga {CorrelationId} in {SagaState}, debited {Debited}, refunded {Refunded}, unlocked {Unlocked}. {Decision}.",
                outcome, transactionId, facts.UserId, facts.ChapterId, facts.Status,
                facts.CorrelationId, facts.SagaState ?? "(none)", facts.Debited, facts.Refunded, facts.Unlocked, decision);
        }
    }
}
