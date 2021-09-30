# avalonia-backport

Tool for doing Avalonia backports

## Command

```
backport
  Avalonia Backport

Usage:
  backport [options]

Options:
  --token <token>            The OAUTH token, with public_repo permission.
  --repository <repository>  The path to the Avalonia repository. Default: current directory
  --after <after>            Skip until after this PR number [default: ]
  --version                  Show version information
  -?, -h, --help             Show help and usage information
```

## Simple Usage

Running:

```
backport --token [token] --repository d:\projects\AvaloniaUI\Avalonia
```

Will first display the PRs that it has identified for backporting. These are PRs that have the `backport-candidate` label but not the `wont-backport` 
label or any label that begins with the string `backported`:

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