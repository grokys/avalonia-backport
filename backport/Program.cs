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
        static async Task<int> Main(string token, DirectoryInfo? repository)
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
                .Where(x => !x.Labels.Contains("backported 0.10.x"))
                .OrderBy(x => x.MergedAt)
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
                var result = repo.CherryPick(commit, signature, new() { Mainline = 1, CommitOnSuccess = true });

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

            return 0;
        }

        private static bool Confirm()
        {
            var key = Console.ReadKey();
            return key.KeyChar != 'Y' || key.KeyChar != 'y';
        }
    }
}
