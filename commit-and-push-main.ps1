<#!powershell
<##
.SYNOPSIS
  Commit local source changes, merge into main, and push main to origin.
.DESCRIPTION
  This script stages tracked modifications, optionally stages untracked files,
  creates a commit, checks out main, merges the current branch into main,
  and pushes main to the origin remote.
.PARAMETER CommitMessage
  Commit message to use for the local commit.
.PARAMETER IncludeUntracked
  If set, stages untracked files as well.
.PARAMETER NoMerge
  If set, does not merge the current branch into main.
.PARAMETER NoPush
  If set, does not push main to the remote.
.EXAMPLE
  .\commit-and-push-main.ps1 -CommitMessage "Update dashboard history JSON" -IncludeUntracked
##>
param(
    [string]$CommitMessage = "Auto commit: update dashboard history JSON serialization and import history deserialization",
    [switch]$IncludeUntracked,
    [switch]$NoMerge,
    [switch]$NoPush
)

function Exit-WithError([string]$message) {
    Write-Host "ERROR: $message" -ForegroundColor Red
    exit 1
}

$scriptPath = $MyInvocation.MyCommand.Path
if (-not $scriptPath) {
    Exit-WithError "Cannot determine script path. Run this script from the repository root or via full path."
}

$repoRoot = Split-Path -Parent $scriptPath
Set-Location $repoRoot

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Exit-WithError "Git is not installed or not available in PATH."
}

$branch = git rev-parse --abbrev-ref HEAD 2>$null
if ($LASTEXITCODE -ne 0) {
    Exit-WithError "Not inside a git repository."
}

Write-Host "Repository root: $repoRoot"
Write-Host "Current branch: $branch"

$untracked = git ls-files --others --exclude-standard
if ($IncludeUntracked -and $untracked) {
    Write-Host "Staging untracked files..."
    git add --all
} else {
    git add -u
}

$staged = git diff --cached --name-only
if (-not $staged) {
    Write-Host "No staged changes to commit."
} else {
    Write-Host "Creating commit with message:`n  $CommitMessage"
    git commit -m "$CommitMessage"
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError "Commit failed. Resolve issues and retry."
    }
}

if ($NoMerge) {
    Write-Host "Skipping merge into main as requested."
    if (-not $NoPush) {
        Write-Host "Pushing current branch to origin/$branch..."
        git push origin HEAD
        if ($LASTEXITCODE -ne 0) {
            Exit-WithError "Push failed."
        }
    }
    exit 0
}

$sourceBranch = $branch
if ($branch -ne 'main') {
    Write-Host "Checking out main..."
    git checkout main
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError "Failed to checkout main."
    }

    Write-Host "Updating origin/main..."
    git fetch origin main
    git merge --ff-only origin/main
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError "Failed to fast-forward main from origin/main. Resolve remote changes first."
    }

    Write-Host "Merging $sourceBranch into main..."
    git merge --no-ff $sourceBranch -m "Merge $sourceBranch into main via script"
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError "Merge failed. Resolve conflicts and retry."
    }
}

if (-not $NoPush) {
    Write-Host "Pushing main to origin..."
    git push origin main
    if ($LASTEXITCODE -ne 0) {
        Exit-WithError "Push failed."
    }
}

if ($branch -ne 'main') {
    Write-Host "Returning to original branch $sourceBranch..."
    git checkout $sourceBranch
}

Write-Host "Done. main is updated and pushed to origin." -ForegroundColor Green
