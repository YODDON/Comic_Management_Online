using System.Text.Json.Serialization;
using SharedKernel.Enums;

namespace WalletAPI.DTOs;

public class CurrencyEntryDto
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
    // Text on the wire, as the old WalletAPI sent and read it (T46).
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TransactionType Type { get; set; }
    public string Description { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public DateTime CreatedAt { get; set; }
}
