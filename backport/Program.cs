using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibGit2Sharp;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;
using Semver;
using Commit = LibGit2Sharp.Commit;
using Repository = LibGit2Sharp.Repository;

namespace Backport
{
    internal class Program
    {
        private static ProductHeaderValue s_productInformation = new ProductHeaderValue("AvaloniaBackport", "0.0.1");
        private static Dictionary<string, ConsoleColor> s_labels = new()
        {
            { "bug", ConsoleColor.Green },
            { "enhancement", ConsoleColor.Blue },
            { "feature", ConsoleColor.Blue },
            { "priority", ConsoleColor.Red },
        };

        static async Task<int> Main(string[] args)
        {
            var tokenOption = new Option<string>(
                name: "--token",
                description: "The OAUTH token.")
            {
                Arity = ArgumentArity.ExactlyOne,
            };

            var repositoryOption = new Option<DirectoryInfo>(
                "--repository",
                () => new DirectoryInfo(Directory.GetCurrentDirectory()),
                "The path to the Avalonia repository.");

            var candidatesOption = new Option<string>(
                "--candidates",
                "The label from which backport candidates are selected. [default: calculated from release branch name]");

            var backportedOption = new Option<string>(
                "--backported",
                "The label with which backported PRs are tagged. [default: calculated from release branch name]");

            var afterOption = new Option<int?>(
                "--after",
                "Skip until after this PR number");

            var tagOption = new Option<SemVersion?>(
                "--tag",
                parseArgument: x => SemVersion.Parse(x.Tokens.Single().Value, SemVersionStyles.Any),
                description: "The current release version tag [default: calculated from current commit tag]");

            var previousTagOption = new Option<SemVersion?>(
                "--previous-tag",
                parseArgument: x => SemVersion.Parse(x.Tokens.Single().Value, SemVersionStyles.Any),
                description: "The previous release version tag [default: calculated from current commit tag]");

            var backportCommand = new Command("cherrypick", "Cherry-pick merged PRs")
            {
                tokenOption,
                repositoryOption,
                candidatesOption,
                backportedOption,
                afterOption,
            };

            backportCommand.AddAlias("cherry-pick");

            var labelCommand = new Command("label", "Label backported PRs")
            {
                tokenOption,
                repositoryOption,
                candidatesOption,
                backportedOption,
                afterOption,
            };

            var changelogCommand = new Command("changelog", "Generate changelog")
            {
                tokenOption,
                repositoryOption,
                tagOption,
                previousTagOption,
            };

            var rootCommand = new RootCommand("Avalonia Backport")
            {
                backportCommand,
                labelCommand,
                changelogCommand,
            };

            backportCommand.SetHandler(
                async (token, repository, candidates, backported, after) =>
                {
                    await Backport(token, repository, candidates, backported, after);
                },
                tokenOption, repositoryOption, candidatesOption, backportedOption, afterOption);

            labelCommand.SetHandler(
                async (token, repository, candidates, backported, after) =>
                {
                    await LabelBackported(token, repository, candidates, backported, after);
                },
                tokenOption, repositoryOption, candidatesOption, backportedOption, afterOption);

            changelogCommand.SetHandler(
                async (token, repository, version, previous) =>
                {
                    await GenerateChangelog(token, repository, version, previous);
                },
                tokenOption, repositoryOption, tagOption, previousTagOption);

            var parser = new CommandLineBuilder(rootCommand)
                .UseDefaults()
                .UseExceptionHandler((e, context) =>
                {
                    if (e is UserCancelledException)
                    {
                        Console.Write("\nUser cancelled.");
                        context.ExitCode = 2;
                    }
                    else
                    {
                        Console.Write("\nError: ");
                        Console.WriteLine(e.Message);
                        context.ExitCode = 1;
                    }
                })
                .Build();

            return await parser.InvokeAsync(args);
        }

        private static async Task Backport(string token, DirectoryInfo repository, string candidates, string backported, int? after)
        {
            var connection = new Connection(s_productInformation, token);
            var repo = new Repository(repository.FullName);
            var signature = repo.Config.BuildSignature(DateTimeOffset.Now);

            GetDefaultLabels(repo, ref candidates, ref backported);

            var pullRequests = await GetBackportCandidates(connection, candidates, backported, after);

            Console.WriteLine($"{pullRequests.Count} PRs to backport:\n");

            foreach (var pr in pullRequests)
            {
                Console.Write($"#{pr.Number} {pr.Title}");

                foreach (var label in pr.Labels)
                {
                    if (s_labels.TryGetValue(label, out var color))
                    {
                        Console.ForegroundColor = color;
                        Console.Write($" [{label}]");
                        Console.ResetColor();
                    }
                }

                Console.WriteLine();
            }

        retry:
            Console.WriteLine("\nEnter a comma-separated list of PR numbers to select, or press Enter to select all:");
            
            if (Console.ReadLine() is { } selectedString && !string.IsNullOrWhiteSpace(selectedString))
            {
                try
                {
                    var selected = selectedString.Split(',').Select(x => int.Parse(x)).ToHashSet();

                    foreach (var i in selected)
                    {
                        if (!pullRequests.Any(x => x.Number == i))
                        {
                            Console.Error.WriteLine($"PR #{i} not found in the list of candidates.");
                            goto retry;
                        }
                    }

                    pullRequests = pullRequests.Where(x => selected.Contains(x.Number)).ToList();
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"Error: {e.Message}");
                    goto retry;
                }
            }

            Console.WriteLine($"\n{pullRequests.Count} PRs will be merged into branch '{repo.Head.FriendlyName}' of '{repository.FullName}'");
            Console.WriteLine($"Signature: {signature}.");
            Console.WriteLine("Press Y to continue, any other key to abort.");

            Confirm();

            foreach (var pr in pullRequests)
            {
                Console.WriteLine($"Merging #{pr.Number} {pr.Title} - {pr.MergeCommit}");
                var commit = repo.Lookup<Commit>(pr.MergeCommit);

                try
                {
                    var options = new CherryPickOptions
                    {
                        CommitOnSuccess = true,
                        IgnoreWhitespaceChange = true,
                    };

                    if (commit.Parents.Count() > 1)
                        options.Mainline = 1;

                    var result = repo.CherryPick(commit, signature, options);

                    if (result.Status == CherryPickStatus.Conflicts)
                    {
                        Console.WriteLine("CONFLICT. Fix the conflict with git 'cherry-pick --continue' and press Y to continue, any other key to abort");
                        Confirm();

                        // We need to refresh the repository here by reading the status, otherwise libgit2 thinks
                        // that we have uncommitted changes.
                        repo.RetrieveStatus();
                    }
                }
                catch (EmptyCommitException e)
                {
                    Console.WriteLine(e.Message);
                }
            }

            Console.WriteLine("SUCCESS! Backport finished.");
        }

        private static async Task LabelBackported(string token, DirectoryInfo repository, string candidates, string backported, int? after)
        {
            var connection = new Connection(s_productInformation, token);
            var repo = new Repository(repository.FullName);

            GetDefaultLabels(repo, ref candidates, ref backported);

            var candidateId = await GetLabelId(connection, candidates);
            var backportedId = await GetLabelId(connection, backported);
            var pullRequests = await GetBackportCandidates(connection, candidates, backported, after);
            var cherryPicked = new List<Candidate>();
            var notCherryPicked = new List<Candidate>();

            Console.WriteLine($"{pullRequests.Count} PRs to check...\n");

            foreach (var pr in ((IEnumerable<Candidate>)pullRequests).Reverse())
            {
                if (!pr.MergedAt.HasValue)
                    Console.WriteLine($"Skipped #{pr.Number} as it has no timestamp.");

                Console.WriteLine($"Checking #{pr.Number} {pr.Title} - {pr.MergeCommit}");

                var mergeCommit = repo.Lookup<Commit>(pr.MergeCommit);
                var mergeCommitTitle = mergeCommit.Message.Split('\n')[0];
                var found = false;

                foreach (var commit in repo.Commits)
                {
                    if (commit.Message.StartsWith(mergeCommitTitle) ||
                        commit.Message.StartsWith($"Merge pull request #{pr.Number}"))
                    {
                        found = true;
                        break;
                    }
                }

                if (found)
                    cherryPicked.Add(pr);
                else
                    notCherryPicked.Add(pr);
            }

            Console.WriteLine($"\nCould not find a backport commit for {notCherryPicked.Count} PRs:\n");

            foreach (var i in notCherryPicked)
            {
                Console.WriteLine($"#{i.Number} {i.Title}");
            }

            Console.WriteLine($"\n{cherryPicked.Count} PRs can be marked as backported:\n");

            foreach (var i in cherryPicked)
            {
                Console.WriteLine($"#{i.Number} {i.Title}");
            }

            Console.WriteLine($"\n{cherryPicked.Count} PRs will be labelled with '{backported}' and have the '{candidates}' label removed.");
            Console.WriteLine("Press Y to continue, any other key to abort.");

            Confirm();

            foreach (var pr in cherryPicked)
            {
                Console.WriteLine($"Labelling #{pr.Number}");

                var query = new Mutation()
                    .Select(m => new
                    {
                        m1 = m.AddLabelsToLabelable(new AddLabelsToLabelableInput
                        {
                            LabelableId = pr.Id,
                            LabelIds = new[] { backportedId },
                        }).Select(x => x.ClientMutationId).Single(),
                        m2 = m.RemoveLabelsFromLabelable(new RemoveLabelsFromLabelableInput
                        {
                            LabelableId = pr.Id,
                            LabelIds = new[] { candidateId },
                        }).Select(x => x.ClientMutationId).Single(),
                    })
                    .Compile();
                await connection.Run(query);
            }
        }

        private static async Task GenerateChangelog(
            string token,
            DirectoryInfo repository,
            SemVersion? current,
            SemVersion? previous)
        {
            var connection = new Connection(s_productInformation, token);
            var repo = new Repository(repository.FullName);

            current ??= GetVersionFromTag(repo);
            previous ??= GetPreviousVersionTag(repo, current);

            List<int> prNumbers;
            List<int>? missingPrNumbers = null;

            // If we're generating notes for a minor version, then PRs may have been backported to both
            // branches separately.
            if (current.Minor != previous.Minor)
            {
                // We're only going to support generating notes for the same major version for the moment.
                if (current.Major != previous.Major)
                    throw new InvalidOperationException("Cannot generate release notes for different major versions.");

                // Get all PRs merged to each of the branches back to the common version.
                var commonVersion = new SemVersion(current.Major);
                var currentPRs = GetMergedPullRequests(repo, current, commonVersion);
                var previousPRs = GetMergedPullRequests(repo, previous, commonVersion);

                // Now we can find the PRs that were merged to the current branch but not the previous branch.
                // Also make a note of any PRs that have been merged to the previous branch but not the current.
                prNumbers = currentPRs.Except(previousPRs).ToList();
                missingPrNumbers = previousPRs.Except(currentPRs).ToList();
            }
            else
            {
                prNumbers = GetMergedPullRequests(repo, current, previous);
            }

            Console.WriteLine($"Found {prNumbers.Count} merge commits between {previous} ... {current}\n\n");

            var fixes = new List<PullRequestModel>();
            var features = new List<PullRequestModel>();
            var misc = new List<PullRequestModel>();

            foreach (var prNumber in prNumbers.Distinct().OrderBy(x => x))
            {
                try
                {
                    var query = new Query()
                        .Repository("Avalonia", "AvaloniaUI")
                        .PullRequest(prNumber)
                        .Select(x => new PullRequestModel
                        {
                            Title = x.Title,
                            Author = x.Author.Login,
                            Url = x.Url,
                            Labels = x.Labels(100, null, null, null, null)
                                      .Select(x => x.Nodes)
                                      .Select(x => x.Name)
                                      .ToList(),
                        })
                        .Compile();
                    var pr = await connection.Run(query);

                    if (pr.Labels?.Contains("bug") == true)
                        fixes.Add(pr);
                    else if (pr.Labels?.Contains("enhancement") == true || pr.Labels?.Contains("feature") == true)
                        features.Add(pr);
                    else
                        misc.Add(pr);
                    
                    Console.Write('.');
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"\nError: {e.Message}");
                }
            }

            Console.WriteLine("\n## What's Changed");

            if (features.Count > 0)
            {
                Console.WriteLine("\n### Features");
                foreach (var pr in features)
                    Console.WriteLine($"* {pr.Title} by @{pr.Author} in {pr.Url}");
            }

            if (fixes.Count > 0)
            {
                Console.WriteLine("\n### Fixes");
                foreach (var pr in fixes)
                    Console.WriteLine($"* {pr.Title} by @{pr.Author} in {pr.Url}");
            }

            if (misc.Count > 0)
            {
                Console.WriteLine("\n### Miscellaneous");
                foreach (var pr in misc)
                    Console.WriteLine($"* {pr.Title} by @{pr.Author} in {pr.Url}");
            }

            if (missingPrNumbers is not null)
            {
                Console.WriteLine("\n## Missing PRs");
                Console.WriteLine(string.Join(", ", missingPrNumbers));
            }
        }

        private static void Confirm()
        {
            var key = Console.ReadKey();
            Console.WriteLine();
            if (key.KeyChar == 'Y' || key.KeyChar == 'y')
                return;
            throw new UserCancelledException();
        }

        private static async Task<List<Candidate>> GetBackportCandidates(
            Connection connection,
            string candidates,
            string backported,
            int? after)
        {
            Console.WriteLine($"Reading pull requests with label '{candidates}'...");

            var query = new Query()
                .Repository("Avalonia", "AvaloniaUI")
                .PullRequests(labels: new[] { candidates }, states: new[] { PullRequestState.Merged })
                .AllPages()
                .Select(pr => new Candidate(
                    pr.Id,
                    pr.Number,
                    pr.Title,
                    pr.Labels(100, null, null, null, null).Select(x => x.Nodes).Select(x => x.Name).ToList(),
                    pr.MergeCommit.Select(x => x.Oid).Single(),
                    pr.MergedAt
                ))
                .Compile();

            return (await connection.Run(query))
                .Where(x => !x.Labels.Contains("wont-backport") && !x.Labels.Contains(backported))
                .OrderBy(x => x.MergedAt)
                .SkipWhile(x => after.HasValue && x.Number != after.Value)
                .Skip(after.HasValue ? 1 : 0)
                .ToList();
        }

        private static void GetDefaultLabels(Repository repo, ref string candidates, ref string backported)
        {
            if (!string.IsNullOrWhiteSpace(candidates) && !string.IsNullOrWhiteSpace(backported))
                return;

            var (major, minor) = GetVersionFromBranch(repo.Head.FriendlyName, "--candidates");

            if (string.IsNullOrWhiteSpace(candidates))
            {
                candidates = $"backport-candidate-{major}.{minor}.x";
                Console.WriteLine($"Backport candidate label calculated as '{candidates}' from current branch '{repo.Head.FriendlyName}'.");
            }

            if (string.IsNullOrWhiteSpace(backported))
            {
                backported = $"backported-{major}.{minor}.x";
                Console.WriteLine($"Backported label calculated as '{backported}' from current branch '{repo.Head.FriendlyName}'.");
            }
        }

        private static async Task<ID> GetLabelId(Connection connection, string name)
        {
            var query = new Query()
                .Repository("Avalonia", "AvaloniaUI")
                .Label(name)
                .Select(x => x.Id)
                .Compile();
            var id = await connection.Run(query);

            if (id.Value is null)
                throw new ArgumentException($"Label '{name}' not found.");

            return id;
        }

        private static List<int> GetMergedPullRequests(
            Repository repo,
            SemVersion current,
            SemVersion previous)
        {
            var fromTag = repo.Tags[previous.ToString()];
            var toTag = repo.Tags[current.ToString()];

            var filter = new CommitFilter
            {
                ExcludeReachableFrom = fromTag,
                IncludeReachableFrom = toTag,
            };

            var commits = repo.Commits.QueryBy(filter).ToList();
            var prMergeCommitRegex = new Regex(@"^Merge pull request #(\d+) from");
            var prSquashMergeCommitRegex = new Regex(@"^.+? \(#(\d+)\)$", RegexOptions.Multiline);
            var result = new List<int>();

            foreach (var commit in commits)
            {
                if (prMergeCommitRegex.Match(commit.Message) is { } match && match.Success)
                    result.Add(int.Parse(match.Groups[1].Value));
                else if (prSquashMergeCommitRegex.Match(commit.Message) is { } squashMatch && squashMatch.Success)
                    result.Add(int.Parse(squashMatch.Groups[1].Value));
            }

            return result;
        }

        private static (int major, int minor) GetVersionFromBranch(string branch, string option)
        {
            var releaseBranchRegex = new Regex("^release/(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$");
            var match = releaseBranchRegex.Match(branch);

            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var major) &&
                int.TryParse(match.Groups[2].Value, out var minor))
            {
                return (major, minor);
            }

            throw new ArgumentException($"Current branch '{branch}' is not a release branch. Specify the --candidates and --backported labels explicitly.");
        }

        private static SemVersion GetVersionFromTag(Repository repo)
        {
            var head = repo.Head.Tip;

            foreach (var tag in repo.Tags)
            {
                if (tag.PeeledTarget is Commit commit &&
                    commit.Sha == head.Sha &&
                    SemVersion.TryParse(tag.FriendlyName, out var version))
                {
                    return version;
                }
            }

            throw new ArgumentException($"The current HEAD '{head.Sha}' is not tagged with a version. Specify the --tag explicitly.");
        }

        private static SemVersion GetPreviousVersionTag(Repository repo, SemVersion current)
        {
            var invalidVersion = new SemVersion(0, 0);
            var result = invalidVersion;

            foreach (var tag in repo.Tags)
            {
                if (tag.PeeledTarget is Commit commit &&
                    SemVersion.TryParse(tag.FriendlyName, out var version) &&
                    version < current && version > result)
                {
                    result = version;
                }
            }

            if (result != invalidVersion)
                return result;

            throw new ArgumentException($"Could not find the previous version to '{current}' from tags in the repository.");
        }

        private record Candidate(ID Id, int Number, string Title, List<string> Labels, string MergeCommit, DateTimeOffset? MergedAt);
    }
}
