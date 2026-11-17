using System.Security.Claims;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PaymentAPI.Controllers;

namespace Payment.Characterization.Tests.Infrastructure;

/// <summary>Builds the real PaymentsController with a signed-in user and/or an Authorization header, without HTTP.</summary>
public static class Controllers
{
    public static PaymentsController Payments(IMediator mediator, string? userId = null, string? authorization = null)
    {
        var http = new DefaultHttpContext();
        if (userId != null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, "Reader") }, "Test"));
        }
        if (authorization != null)
        {
            http.Request.Headers.Authorization = authorization;
        }
        return new PaymentsController(mediator) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    /// <summary>Status code of an IActionResult the way MVC would send it (ObjectResult without a status = 200).</summary>
    public static int StatusOf(IActionResult result) => result switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => throw new InvalidOperationException($"Unexpected result {result.GetType().Name}"),
    };
}
