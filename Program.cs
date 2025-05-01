using Octokit;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.dev.json", optional: false)
    .Build();

var token = config["GitHub:Token"];
var sourceRepo = config["GitHub:SourceRepo"].Split('/');
var targetRepo = config["GitHub:TargetRepo"].Split('/');
int initialDelay = 2001;
int retryDelay = initialDelay;

var github = new GitHubClient(new ProductHeaderValue("GitHubIssueMigrator"))
{
    Credentials = new Credentials(token)
};

var sourceOwner = sourceRepo[0];
var sourceName = sourceRepo[1];
var targetOwner = targetRepo[0];
var targetName = targetRepo[1];

Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  Migrating issues");

var issueRequest = new RepositoryIssueRequest
{
    State = ItemStateFilter.All,
    Filter = IssueFilter.All
};

var issues = await github.Issue.GetAllForRepository(sourceOwner, sourceName, issueRequest);
var existingIssues = await github.Issue.GetAllForRepository(targetOwner, targetName, issueRequest);

var i = 1;
foreach (var issue in issues.Reverse())
{
    // if (issue.PullRequest != null) continue; // skip PRs
    var type = (issue.PullRequest != null) ? "PR" : "Issue";

    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {i++}) Migrating {type} #{issue.Number}...\n\tTitle: {issue.Title}");
    // Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  {JsonSerializer.Serialize(issue, new JsonSerializerOptions { WriteIndented = true })}");
    
    var migrationMarker = $"{type} Migrated from [{sourceOwner}/{sourceName}#{issue.Number}]({issue.HtmlUrl})";
    bool alreadyMigrated = existingIssues.Any(i => i.Body?.Contains(migrationMarker) == true);
    if (alreadyMigrated)
    {
        // check if the status is the same
        var existingIssue = existingIssues.First(i => i.Body?.Contains(migrationMarker) == true);
        if (existingIssue.State == issue.State)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]  Skipping {type} #{issue.Number} (already migrated to #{existingIssue.Number} and status is the same)");
            continue;
        }
        else
        {
            // update the status of the existing issue
            var update = new IssueUpdate
            {
                State = issue.State.Value
            };
            await GithubRateLimiter(async () => await github.Issue.Update(targetOwner, targetName, existingIssue.Number, update));
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Updated status of {type} #{existingIssue.Number} to {issue.State}");
        }
        Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Skipping {type} #{issue.Number} (already migrated to #{existingIssue.Number})");
        continue;
    }

    var newIssue = new NewIssue(issue.Title)
    {
        Body = $"{migrationMarker}\n\n{issue.Body}"
    };

    foreach (var label in issue.Labels)
        newIssue.Labels.Add(label.Name);

    var created = await GithubRateLimiter(async () => await github.Issue.Create(targetOwner, targetName, newIssue));

    // Migrate comments
    var comments = await github.Issue.Comment.GetAllForIssue(sourceOwner, sourceName, issue.Number);
    foreach (var comment in comments)
    {
        var commentBody = $"**Original comment by {comment.User.Login} on {comment.CreatedAt:yyyy-MM-dd}:**\n\n{comment.Body}";
        await GithubRateLimiter(async () => await github.Issue.Comment.Create(targetOwner, targetName, created.Number, comment.Body));
    }

    // If original issue was closed, update the state
    if (issue.State.Value == ItemState.Closed)
    {
        var update = new IssueUpdate
        {
            State = ItemState.Closed
        };
        await GithubRateLimiter(async () => await github.Issue.Update(targetOwner, targetName, created.Number, update));
    }

    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Migrated issue #{issue.Number} -> #{created.Number}");
}

async Task<T> GithubRateLimiter<T>(Func<Task<T>> func)
{
    while(true){
        try
        {
            var result = await func();
            // await github.Issue.Update(targetOwner, targetName, created.Number, update);
            await Task.Delay(retryDelay);
            retryDelay = initialDelay;
            return result;
        }
        catch (SecondaryRateLimitExceededException)
        {
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Rate limit hit. Waiting before retrying...");
            retryDelay *= 2; // Exponential backoff
            await Task.Delay(retryDelay);
        }
    }
}