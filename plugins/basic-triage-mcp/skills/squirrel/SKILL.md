---
name: squirrel
description: A skill for determining who gets the squirrel or squirrel of chain
---

The squirrel of chain is a totem that is given, in a funny ritualizied manner, to the person who is deemed most responsible for the most recent pipeline failure in main. Your job is to determine who gets the squirrel of chain.

To do this look at the builds of main for the given repository, dotnet/roslyn if none is given. Then do the following: 

1. Find the most recent build that failed.
2. Find the collection of pull requests merged into `main` since the last successful build.
3. Make an assessment of which pull request is most likely to have caused the failure. Consider factors such as:
   - The files changed in the pull request and whether they are related to the failure.
   - The test failures and whether they are related to the changes in the pull request.
   - The history of the author of the pull request and whether they have a history of causing failures.
4. Do not consider builds where all of the failures are likely infrastructure issues, such as timeouts or provisioning failures. Focus on builds where the failures are likely caused by code changes. 

Keep going down the list of failed builds until you find a build where you can identify a likely culprit pull request. Do not stop until you find a person to give the squirrel of chain to.

Provide the name of the author of the pull request, a link to the pull request and a brief explanation of why you think this pull request is the most likely cause of the failure.

