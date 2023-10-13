using System;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LibGit2Sharp;
using Octokit.GraphQL;
using Octokit.GraphQL.Model;
using Commit = LibGit2Sharp.Commit;
using Repository = LibGit2Sharp.Repository;

namespace Backport
{
    internal class Program
    {
        /// <summary>
        /// Avalonia Backport
        /// </summary>
        /// <param name="token">The OAUTH token, with public_repo permission.</param>
        /// <param name="repository">The path to the Avalonia repository. Default: current directory</param>
        /// <param name="candidates">The label from which backport candidates are selected.</param>
        /// <param name="after">Skip until after this PR number</param>
        static async Task<int> Main(string[] args)
        {
            var tokenOption = new Option<string>(
                name: "--token",
                description: "The OAUTH token, with public_repo permission.")
                {
                    Arity = ArgumentArity.ExactlyOne,
                };

            var repositoryOption = new Option<DirectoryInfo>(
                "--repository",
                () => new DirectoryInfo(Directory.GetCurrentDirectory()),
                "The path to the Avalonia repository. Default: current directory");

            var candidatesOption = new Option<string>(
                "--candidates",
                "The label from which backport candidates are selected.");

            var afterOption = new Option<int?>(
                "--after",
                "Skip until after this PR number");

            var backportCommand = new Command("cherrypick", "Cherry-pick merged PRs")
            {
                tokenOption,
                repositoryOption,
                candidatesOption,
                afterOption,
            };

            backportCommand.AddAlias("cherry-pick");

            var rootCommand = new RootCommand("Avalonia Backport")
            {
                backportCommand,
            };

            backportCommand.SetHandler(
                async (token, repository, candidates, after) =>
                {
                    await Backport(token, repository, candidates, after);
                }, 
                tokenOption, repositoryOption, candidatesOption, afterOption);

            return await rootCommand.InvokeAsync(args);
        }

        private static async Task<int> Backport(string token, DirectoryInfo repository, string candidates, int? after)
        {
            var productInformation = new ProductHeaderValue("AvaloniaBackport", "0.0.1");
            var connection = new Connection(productInformation, token);
            Repository repo;
            Signature signature;

            repository ??= new DirectoryInfo(Directory.GetCurrentDirectory());

            try
            {
                repo = new Repository(repository.FullName);
                signature = repo.Config.BuildSignature(DateTimeOffset.Now);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                return 1;
            }

            if (string.IsNullOrWhiteSpace(candidates))
            {
                var releaseBranchRegex = new Regex("^release/(0|[1-9]\\d*)\\.(0|[1-9]\\d*)$");
                var match = releaseBranchRegex.Match(repo.Head.FriendlyName);

                if (!match.Success)
                {
                    Console.WriteLine($"Error: no label supplied and current branch ({repo.Head.FriendlyName}) is not a release branch.");
                    return 1;
                }

                candidates = $"backport-candidate-{match.Groups[1].Value}.{match.Groups[2].Value}.x";
                Console.WriteLine($"Label calculated as '{candidates}' from current branch '{repo.Head.FriendlyName}'.");
            }

            Console.WriteLine($"Reading pull requests with label {candidates}...");

            var query = new Query()
                .Repository("Avalonia", "AvaloniaUI")
                .PullRequests(labels: new[] { candidates }, states: new[] { PullRequestState.Merged })
                .AllPages()
                .Select(pr => new
                {
                    pr.Number,
                    pr.Title,
                    Labels = pr.Labels(100, null, null, null, null).Select(x => x.Nodes).Select(x => x.Name).ToList(),
                    MergeCommit = pr.MergeCommit.Select(x => x.Oid).Single(),
                    pr.MergedAt,
                })
                .Compile();

            var toMerge = (await connection.Run(query))
                .Where(x => !x.Labels.Contains("wont-backport") && !x.Labels.Any(x => x.StartsWith("backported")))
                .OrderBy(x => x.MergedAt)
                .SkipWhile(x => after.HasValue && x.Number != after.Value)
                .Skip(after.HasValue ? 1 : 0)
                .ToList();

            Console.WriteLine($"{toMerge.Count} PRs to backport:\n");

            foreach (var pr in toMerge)
            {
                Console.WriteLine($"#{pr.Number} {pr.Title}");
            }

            Console.WriteLine($"\n{toMerge.Count} PRs will be merged into branch '{repo.Head.FriendlyName}' of '{repository.FullName}'");
            Console.WriteLine($"Signature: {signature}.");
            Console.WriteLine("Press Y to continue, any other key to abort.");

            if (!Confirm())
            {
                Console.WriteLine("\nUser cancelled.");
                return 2;
            }

            foreach (var pr in toMerge)
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
                        Console.WriteLine("CONFLICT. Fix the conflict and press Y to continue, any other key to abort");

                        if (!Confirm())
                        {
                            Console.WriteLine("\nUser cancelled.");
                            return 2;
                        }

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
            return 0;
        }

        private static bool Confirm()
        {
            var key = Console.ReadKey();
            var result = key.KeyChar == 'Y' || key.KeyChar == 'y';
            Console.WriteLine();
            return result;
        }
    }
}
