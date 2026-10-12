// Reading the current branch straight from a repository's files. Free of the vscode module so it can be
// tested in plain Node; extension.ts supplies the file reads, which go through VS Code's file system and
// so reach WSL, SSH and container workspaces that the built-in Git extension's API cannot from here.

/** The branch named by a HEAD file, or a short commit id when HEAD is detached. */
export function branchFromHead(head: string): string | null {
	const text = head.trim();
	const ref = /^ref:\s*refs\/heads\/(.+)$/.exec(text);
	if (ref) {
		return ref[1];
	}

	return /^[0-9a-f]{40,64}$/i.test(text) ? text.slice(0, 7) : null;
}

/** For a `.git` that is a file (worktrees, submodules): the git directory it points to, as written. */
export function gitDirFromDotGitFile(contents: string): string | null {
	const match = /^gitdir:\s*(.+)$/m.exec(contents);
	return match ? match[1].trim() : null;
}
