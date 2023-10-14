# avalonia-backport

Tool for doing Avalonia backports

## Command

```
Description:
  Avalonia Backport

Usage:
  backport [command] [options]

Options:
  --version       Show version information
  -?, -h, --help  Show help and usage information

Commands:
  cherry-pick, cherrypick  Cherry-pick merged PRs
  label                    Label backported PRs```
```

## Cherry picking

Running the following where a release branch is checked out in the Avalonia repository:

```
backport cherrypick --token [token] --repository d:\projects\AvaloniaUI\Avalonia
```

Will first display the PRs that it has identified for backporting. These are PRs that have a `backport-candidate-MAJ-MIN-x` label but not the `wont-backport` or `backported-MAJ-MIN-x` where `MAJ-MIN` is the current release branch version:

```
4 PRs to backport:

#6456 Revert "Fix skia OpacityMask push and pop methods"
#6457 Don't display warning when WinUICompositorConnection succeeds.
#6226 ContentPresenter should create child without content, if template was set
#6392 [Menu] [Interaction] Allow end user to change menu show delay globally

4 PRs will be merged into branch 'stable/0.10.x' of 'd:\projects\AvaloniaUI\Avalonia'
Signature: Steven Kirk <grokys@gmail.com>.
Press Y to continue, any other key to abort.
```

Pressing `y` will cherry pick the merge commits for each of these PRs into the currently checked-out branch in the repository. If a conflict is encountered, the following will be displayed:

```
Merging #6576 Add GeometryGroup and CombinedGeometry - f51efd3318ae6413fa83c78248b8afd860e5ab7b
CONFLICT. Fix the conflict and press Y to continue, any other key to abort
```

In this case, fix the conflict and run `git cherry-pick --continue` in another terminal to commit. You can then press `y` at the prompt to continue.

## Continuing Backporting

If you want to continue from a previously merged PR, you can supply the `--after` option. For example if running `git log` shows:

```
commit 1193af3689bf6f243c44d2778b449cd11fd45fd9
Author: Dan Walmsley <dan@walms.co.uk>
Date:   Tue Sep 28 12:05:58 2021 +0100

    Merge pull request #6652 from AvaloniaUI/feature/fbdev-customization

    Add a few customization points to Linux Framebuffer backend.
```

You can run:

```
backport --token [token] --repository d:\projects\AvaloniaUI\Avalonia --after 6552
```

To merge all PRs subsequent to 6552.

## After Completion

Once all PRs have been merged to the branch, you need to:

- Test!
- Push the branch
- Run the `label` command to label the PRs as backported

## Labeling

Running the following where a release branch is checked out in the Avalonia repository:

```
backport label --token [token] --repository d:\projects\AvaloniaUI\Avalonia
```

Will first display PRs that are tagged with a `backport-candidate` label for the current release branch, and already appear on the current release branch.

After displaying a list of PRs, press `Y` to label these PRs with `backported-MAJ-MIN-x` where `MAJ-MIN` is the current release branch.

## Release Branches

A release branch is a branch with the format `release/MAJ.MIN`, for example `release/11.0` in which case MAJ=11 and MIN=0. On such branches the backport
candidate and backported labels can be calculated automatically. If they cannot be calculated, they can be supplied as arguments to the `cherrypick` and
`label` commands.