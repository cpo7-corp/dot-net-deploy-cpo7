using Microsoft.AspNetCore.Mvc;
using NET.Deploy.Api.Logic.Git;
using NET.Deploy.Api.Logic.Settings;

namespace NET.Deploy.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class GitController(GitLogic gitLogic, SettingsLogic settingsLogic) : ControllerBase
{
    [HttpPost("pull")]
    public async Task<IActionResult> Pull([FromQuery] string repoUrl, [FromQuery] string branch = "main")
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return BadRequest("RepoUrl is required.");

        var settings = await settingsLogic.GetAsync();
        var success = await gitLogic.PullAsync(settings.Git, repoUrl, branch);
        return success ? Ok("Pull successful.") : StatusCode(500, "Git pull failed.");
    }

    [HttpGet("commits")]
    public async Task<IActionResult> GetCommits(
        [FromQuery] string repoUrl,
        [FromQuery] string branch = "main",
        [FromQuery] int skip = 0,
        [FromQuery] int take = 20)
    {
        if (string.IsNullOrWhiteSpace(repoUrl)) return BadRequest("RepoUrl is required.");
        if (skip < 0) return BadRequest("Skip must be zero or greater.");
        if (take <= 0 || take > 100) return BadRequest("Take must be between 1 and 100.");

        var settings = await settingsLogic.GetAsync();
        var (parsedUrl, _, _) = gitLogic.ParseGitUrl(repoUrl);
        var repoPath = gitLogic.GetRepoLocalPath(settings.Git, parsedUrl);

        var commits = await gitLogic.ListRecentCommitsAsync(repoPath, branch, skip, take + 1);
        var hasMore = commits.Count > take;
        if (hasMore)
        {
            commits = commits.Take(take).ToList();
        }

        return Ok(new
        {
            items = commits,
            skip,
            take,
            hasMore
        });
    }
}
