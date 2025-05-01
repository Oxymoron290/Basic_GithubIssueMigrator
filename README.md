# Github Issue Migrator

This is a very basic migrator for moving github issues from one repository to another. 

- It does not guarantee issue number matching
- It does not guarantee the time issues/comments were submitted or reflecting properly who submitted issues/comments
- It attempts a certain level of idempotence
- It will recreate PRs as issues minus the diffs/code
- It currently does not migrate project information over.
- It attempts to implement an exponential backoff to alleviate the rate limiter.

## Set up

In git hub you need to create a "personal access token (Classic)" with the `repo` permission enabled. Set the values in your appsettings.json and run the application. No command line arguments are available at this time.
