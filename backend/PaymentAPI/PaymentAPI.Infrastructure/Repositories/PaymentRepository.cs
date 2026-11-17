using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using PaymentAPI.Data;
using PaymentAPI.Domain.Entities;
using PaymentAPI.Entities;
using PaymentAPI.Interfaces;

namespace PaymentAPI.Repositories
{
    public class PaymentRepository : IPaymentRepository
    {
        private readonly PaymentDbContext _context;
        private IDbContextTransaction? _transaction;

        public PaymentRepository(PaymentDbContext context)
        {
            _context = context;
        }

        public async Task<List<Transaction>> GetUserTransactionsAsync(int userId)
        {
            return await _context.Transactions
                .Where(t => t.UserId == userId && t.Status == SharedKernel.Enums.TransactionStatus.Completed)
                .ToListAsync();
        }

        public async Task<(List<Transaction> Items, int TotalCount)> GetTransactionsAsync(
            int? userId,
            string? type,
            string? status,
            string? search,
            int pageNumber,
            int pageSize)
        {
            var query = _context.Transactions.AsQueryable();

            if (userId.HasValue)
            {
                query = query.Where(t => t.UserId == userId.Value);
            }

            if (!string.IsNullOrEmpty(type) && Enum.TryParse<SharedKernel.Enums.TransactionType>(type, true, out var transactionType))
            {
                query = query.Where(t => t.Type == transactionType);
            }

            if (!string.IsNullOrEmpty(status) && Enum.TryParse<SharedKernel.Enums.TransactionStatus>(status, true, out var transactionStatus))
            {
                query = query.Where(t => t.Status == transactionStatus);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var keyword = search.Trim();
                var isUserId = int.TryParse(keyword, out var searchedUserId);
                query = query.Where(t =>
                    t.TransactionCode.Contains(keyword) ||
                    (isUserId && t.UserId == searchedUserId));
            }

            var totalCount = await query.CountAsync();
            var items = await query
                .OrderByDescending(t => t.CreatedAt)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return (items, totalCount);
        }

        public async Task<Transaction?> GetTransactionByIdAsync(Guid id)
        {
            return await _context.Transactions.FindAsync(id);
        }

        public async Task<Transaction?> GetTransactionByCodeAsync(string transactionCode)
        {
            return await _context.Transactions.FirstOrDefaultAsync(t => t.TransactionCode == transactionCode);
        }

        public async Task<Transaction?> GetTransactionByNotePrefixAsync(string notePrefix)
        {
            return await _context.Transactions.FirstOrDefaultAsync(t => t.Note != null && t.Note.StartsWith(notePrefix));
        }

        public async Task<UserPurchase?> GetUserPurchaseAsync(int userId, Guid chapterId)
        {
            return await _context.UserPurchases
                .FirstOrDefaultAsync(u => u.UserId == userId && u.ChapterId == chapterId);
        }

        public async Task AddTransactionAsync(Transaction transaction)
        {
            _context.Transactions.Add(transaction);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateTransactionAsync(Transaction transaction)
        {
            _context.Transactions.Update(transaction);
            await _context.SaveChangesAsync();
        }

        public async Task AddUserPurchaseAsync(UserPurchase userPurchase)
        {
            _context.UserPurchases.Add(userPurchase);
            await _context.SaveChangesAsync();
        }

        public async Task BeginTransactionAsync()
        {
            _transaction = await _context.Database.BeginTransactionAsync();
        }

        public async Task CommitTransactionAsync()
        {
            if (_transaction != null)
            {
                await _transaction.CommitAsync();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public async Task RollbackTransactionAsync()
        {
            if (_transaction != null)
            {
                await _transaction.RollbackAsync();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
        }

        public async Task SaveFailedTransactionAsync(Transaction transaction)
        {
            transaction.Status = SharedKernel.Enums.TransactionStatus.Failed;
            // The transaction is already committed: update it, never insert it again.
            _context.Transactions.Update(transaction);
            await _context.SaveChangesAsync();
        }

        // ---- Purchase recovery job (T23) ----

        public async Task<List<Guid>> GetPendingPurchaseIdsAsync(DateTime createdBefore, int limit)
        {
            return await _context.Transactions
                .Where(t => t.Type == SharedKernel.Enums.TransactionType.Purchase
                            && t.Status == SharedKernel.Enums.TransactionStatus.Pending
                            && t.CreatedAt < createdBefore)
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<List<Guid>> GetFailedPurchaseIdsByNotePrefixAsync(string notePrefix, DateTime createdAfter, int limit)
        {
            return await _context.Transactions
                .Where(t => t.Type == SharedKernel.Enums.TransactionType.Purchase
                            && t.Status == SharedKernel.Enums.TransactionStatus.Failed
                            && t.CreatedAt > createdAfter
                            && t.Note != null && t.Note.StartsWith(notePrefix))
                .OrderBy(t => t.CreatedAt)
                .Select(t => t.Id)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<List<Guid>> GetSagaTransactionIdsAsync(IReadOnlyCollection<string> states, DateTime lastActivityBefore, int limit)
        {
            return await _context.PurchaseStates
                .Where(s => states.Contains(s.CurrentState) && (s.UpdatedAt ?? s.CreatedAt) < lastActivityBefore)
                .OrderBy(s => s.CreatedAt)
                .Select(s => s.TransactionId)
                .Take(limit)
                .ToListAsync();
        }

        public async Task<PurchaseState?> GetPurchaseStateAsync(Guid transactionId)
        {
            return await _context.PurchaseStates.AsNoTracking().FirstOrDefaultAsync(s => s.TransactionId == transactionId);
        }

        public async Task<PurchaseState?> LockPurchaseStateAsync(Guid transactionId)
        {
            // Table name from the EF model (not user input); the id is a parameter.
            var sql = "SELECT * FROM [" + _context.Model.FindEntityType(typeof(PurchaseState))!.GetTableName()
                      + "] WITH (UPDLOCK, ROWLOCK) WHERE [TransactionId] = {0}";
            return (await _context.PurchaseStates
                    .FromSqlRaw(sql, transactionId)
                    .ToListAsync())
                .FirstOrDefault();
        }

        public async Task<Transaction?> LockTransactionAsync(Guid id)
        {
            var sql = "SELECT * FROM [" + _context.Model.FindEntityType(typeof(Transaction))!.GetTableName()
                      + "] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {0}";
            return (await _context.Transactions
                    .FromSqlRaw(sql, id)
                    .ToListAsync())
                .FirstOrDefault();
        }

        public async Task UpdatePurchaseStateAsync(PurchaseState state)
        {
            _context.PurchaseStates.Update(state);
            await _context.SaveChangesAsync();
        }

        public async Task RemovePurchaseStateAsync(PurchaseState state)
        {
            _context.PurchaseStates.Remove(state);
            await _context.SaveChangesAsync();
        }
    }
}
