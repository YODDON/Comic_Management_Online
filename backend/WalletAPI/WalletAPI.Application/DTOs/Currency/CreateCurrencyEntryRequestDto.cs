using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SharedKernel.Enums;

namespace WalletAPI.DTOs;

public class CreateCurrencyEntryRequestDto
{
    public int UserId { get; set; }

    public decimal Amount { get; set; }

    // Text on the wire, as the old WalletAPI sent and read it (T46).
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TransactionType Type { get; set; }

    [Required]
    [MaxLength(500)]
    public string Description { get; set; } = string.Empty;
}
