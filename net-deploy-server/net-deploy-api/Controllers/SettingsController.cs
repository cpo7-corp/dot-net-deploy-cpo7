using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Data.Entities;
using NET.Deploy.Api.Logic.Settings;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SettingsController(SettingsLogic settingsLogic) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AppSettingsDB>> Get() =>
        Ok(await settingsLogic.GetAsync());

    [HttpPut]
    public async Task<ActionResult<AppSettingsDB>> Save([FromBody] AppSettingsDB settings) =>
        Ok(await settingsLogic.SaveAsync(settings));
}
