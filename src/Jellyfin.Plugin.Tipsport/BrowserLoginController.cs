using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Tipsport;

[ApiController]
[Route("Plugins/Tipsport/login")]
public sealed class BrowserLoginController(BrowserLogin login) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    public BrowserLoginStatus Status() => login.Status;

    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Begin()
    {
        try { return Ok(login.Begin(Plugin.Instance?.Configuration.Region ?? "CZ")); }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }

    [HttpPost("credentials")]
    [RequestSizeLimit(8192)]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult Credentials([FromBody] PasswordLoginRequest request)
    {
        if (!Request.IsHttps) return BadRequest(new { Message = "Jméno a heslo zadávejte pouze přes HTTPS adresu Jellyfinu." });
        try { return Ok(login.BeginCredentials(Plugin.Instance?.Configuration.Region ?? "CZ", request.Username ?? "", request.Password ?? "", request.Remember)); }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
        finally { request.Password = null; }
    }

    [HttpDelete("credentials")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public IActionResult ForgetCredentials()
    {
        try { login.ForgetCredentials(); return Ok(login.Status); }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }

    [HttpPost("renew")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<IActionResult> Renew(CancellationToken cancellationToken)
    {
        try
        {
            var region = Plugin.Instance?.Configuration.Region ?? "CZ";
            if (login.Status.AutomaticLoginPaused) return Ok(login.BeginSavedCredentials(region));
            if (await login.TryRecover(region, cancellationToken).ConfigureAwait(false)) return Ok(login.Status);
            return BadRequest(new { Message = "Uložené přihlášení se nyní nepodařilo obnovit. " + login.Status.Message });
        }
        catch (TipsportException e) { return BadRequest(new { Message = e.Message }); }
    }
}

public sealed class PasswordLoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool Remember { get; set; }
}
