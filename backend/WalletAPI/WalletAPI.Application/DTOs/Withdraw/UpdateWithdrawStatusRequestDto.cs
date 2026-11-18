using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using SharedKernel.Enums;

namespace WalletAPI.DTOs;

public class UpdateWithdrawStatusRequestDto
{
    // Text on the wire, as the old WalletAPI sent and read it (T46).
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public WithdrawStatus Status { get; set; }

    [MaxLength(1000)]
    public string? Note { get; set; }
}
