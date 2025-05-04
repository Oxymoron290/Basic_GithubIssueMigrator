using Octokit;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using System.Text.Json;

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.dev.json", optional: false)
    .Build();

var totalPoints = 0;
var token = config["GitHub:Token"];
var sourceRepo = config["GitHub:SourceRepo"].Split('/');
var targetRepo = config["GitHub:TargetRepo"].Split('/');

var github = new GitHubClient(new ProductHeaderValue("GitHubIssueMigrator"))
{
    Credentials = new Credentials(token)
};

var sourceOwner = sourceRepo[0];
var sourceName = sourceRepo[1];
var targetOwner = targetRepo[0];
var targetName = targetRepo[1];

Log($"Migrating issues");

var issueRequest = new RepositoryIssueRequest
{
    State = ItemStateFilter.All,
    Filter = IssueFilter.All
};

var issues = await github.Issue.GetAllForRepository(sourceOwner, sourceName, issueRequest);
totalPoints += 1;
var existingIssues = await github.Issue.GetAllForRepository(targetOwner, targetName, issueRequest);
totalPoints += 1;

var i = 1;
foreach (var issue in issues.Reverse())
{
    // if (issue.PullRequest != null) continue; // skip PRs
    var type = (issue.PullRequest != null) ? "PR" : "Issue";

    Log($"{i++}) Migrating {type} #{issue.Number}...\n\tTitle: {issue.Title}");
    // Log($"{JsonSerializer.Serialize(issue, new JsonSerializerOptions { WriteIndented = true })}");
    
    var migrationMarker = $"{type} Migrated from [{sourceOwner}/{sourceName}#{issue.Number}]({issue.HtmlUrl})";
    bool alreadyMigrated = existingIssues.Any(i => i.Body?.Contains(migrationMarker) == true);
    if (alreadyMigrated)
    {
        // check if the status is the same
        var existingIssue = existingIssues.First(i => i.Body?.Contains(migrationMarker) == true);
        if (existingIssue.State == issue.State)
        {
            Log($"Skipping {type} #{issue.Number} (already migrated to #{existingIssue.Number} and status is the same)");
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
            totalPoints += 5;
            Log($"Updated status of {type} #{existingIssue.Number} to {issue.State}");
        }
        Log($"Skipping {type} #{issue.Number} (already migrated to #{existingIssue.Number})");
        continue;
    }

    var newIssue = new NewIssue(issue.Title)
    {
        Body = $"{migrationMarker}\n\n{issue.Body}"
    };

    foreach (var label in issue.Labels)
        newIssue.Labels.Add(label.Name);

    var created = await GithubRateLimiter(async () => await github.Issue.Create(targetOwner, targetName, newIssue));
    totalPoints += 5;

    // Migrate comments
    var comments = await github.Issue.Comment.GetAllForIssue(sourceOwner, sourceName, issue.Number);
    totalPoints += 1;
    foreach (var comment in comments)
    {
        var commentBody = $"**Original comment by {comment.User.Login} on {comment.CreatedAt:yyyy-MM-dd}:**\n\n{comment.Body}";
        await GithubRateLimiter(async () => await github.Issue.Comment.Create(targetOwner, targetName, created.Number, comment.Body));
        totalPoints += 5;
    }

    // If original issue was closed, update the state
    if (issue.State.Value == ItemState.Closed)
    {
        var update = new IssueUpdate
        {
            State = ItemState.Closed
        };
        await GithubRateLimiter(async () => await github.Issue.Update(targetOwner, targetName, created.Number, update));
        totalPoints += 5;
    }

    Log($"Migrated issue #{issue.Number} -> #{created.Number}");
}

void Log(string message)
{
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({totalPoints}) {message}");
}

async Task<T> GithubRateLimiter<T>(Func<Task<T>> func)
{
    int retryCount = 0;
    int secondaryRetries = 0;
    while(retryCount <= 5)
    {
        retryCount++;
        try
        {
            var result = await func();
            secondaryRetries = 0;
            retryCount = 0;
            Task.Delay(5000).Wait(); // wait 5 seconds between requests to avoid hitting the rate limit
            return result;
        }
        catch(ApiException ex)
        {
            if(ex.HttpResponse.StatusCode == System.Net.HttpStatusCode.Forbidden || 
            ex.HttpResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                // Log($"Rate-limited. Waiting {ex.HttpResponse.StatusCode} ({ex.Message})...");
                foreach(var header in ex.HttpResponse.Headers)
                {
                    Log($"\t {header.Key}: {header.Value}");
                }
                retryCount++;
                var headers = ex.HttpResponse.Headers;

                if (headers.TryGetValue("Retry-After", out var retryAfter))
                {
                    if (int.TryParse(retryAfter, out var seconds))
                    {
                        Log($"Rate-limited. Waiting {seconds} seconds...");
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        continue;
                    }
                }

                if (headers.TryGetValue("X-RateLimit-Remaining", out var remaining) && remaining == "0")
                {
                    if (headers.TryGetValue("X-RateLimit-Reset", out var reset))
                    {
                        var resetTime = DateTimeOffset.FromUnixTimeSeconds(long.Parse(reset));
                        var delay = resetTime - DateTimeOffset.UtcNow;
                        if (delay.TotalSeconds > 0)
                        {
                            Log($"Primary rate-limited. Waiting until {resetTime:u} ({delay.TotalSeconds:F0} seconds)...");
                            await Task.Delay(delay);
                            continue;
                        }
                    }
                }

                var secondaryDelay = 60 * (int)Math.Pow(2, secondaryRetries++);
                Log($"Secondary rate-limited. Waiting {secondaryDelay} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(secondaryDelay));
                continue;
            }
            throw;
        }
    }
    throw new Exception($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Rretry limits exceeded. Exiting...");
}