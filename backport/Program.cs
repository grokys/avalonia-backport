using System;
using System.IO;
using System.Linq;
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
        /// <param name="after">Skip until after this PR number</param>
        static async Task<int> Main(string token, DirectoryInfo? repository, int? after = null)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                Console.WriteLine("Error: token not supplied.");
                return 1;
            };

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

            Console.WriteLine("Reading pull requests...");

            var query = new Query()
                .Repository("Avalonia", "AvaloniaUI")
                .PullRequests(labels: new[] { "backport-candidate" }, states: new[] { PullRequestState.Merged })
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
                Console.WriteLine("\nUser canceled.");
                return 2;
            }

            foreach (var pr in toMerge)
            {
                Console.WriteLine($"Merging #{pr.Number} {pr.Title} - {pr.MergeCommit}");
                var commit = repo.Lookup<Commit>(pr.MergeCommit);

                try
                {
                    var options = new CherryPickOptions { CommitOnSuccess = true };

                    if (commit.Parents.Count() > 1)
                        options.Mainline = 1;

                    var result = repo.CherryPick(commit, signature, options);

                    if (result.Status == CherryPickStatus.Conflicts)
                    {
                        Console.WriteLine("CONFLICT. Fix the conflict and press Y to continue, any other key to abort");

                        if (!Confirm())
                        {
                            Console.WriteLine("\nUser canceled.");
                            return 2;
                        }
                    }
                }
                catch (EmptyCommitException e)
                {
                    Console.WriteLine(e.Message);
                }
            }

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
