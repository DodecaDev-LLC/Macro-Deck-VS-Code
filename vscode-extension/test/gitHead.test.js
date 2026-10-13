const test = require("node:test");
const assert = require("node:assert/strict");
const { branchFromHead, gitDirFromDotGitFile } = require("../out/gitHead.js");

test("a symbolic HEAD names its branch, including branches with slashes", () => {
	assert.equal(branchFromHead("ref: refs/heads/main\n"), "main");
	assert.equal(branchFromHead("ref: refs/heads/feature/vscode-bridge\n"), "feature/vscode-bridge");
});

test("a detached HEAD shows a short commit id", () => {
	assert.equal(branchFromHead("1ecea1993b7fce257ffbd686c070948b5e07eda6\n"), "1ecea19");
});

test("anything else is no branch", () => {
	assert.equal(branchFromHead(""), null);
	assert.equal(branchFromHead("garbage"), null);
});

test("a worktree's .git file points at its git directory", () => {
	assert.equal(gitDirFromDotGitFile("gitdir: /home/me/repo/.git/worktrees/wt\n"), "/home/me/repo/.git/worktrees/wt");
	assert.equal(gitDirFromDotGitFile("gitdir: ../.git/modules/sub\n"), "../.git/modules/sub");
	assert.equal(gitDirFromDotGitFile("nothing here"), null);
});
