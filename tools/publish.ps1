#Requires -Version 7
<#
.SYNOPSIS
  Publishes the private history to the public repository as one squash commit.

.DESCRIPTION
  Builds a commit whose tree is exactly <Ref>'s tree and whose parent is the public main branch, authored with
  the public identity (git config publish.name / publish.email). The private commits never reach the public
  repository. Paths listed in .publishignore (one per line, # for comments) are left out of the published tree. Without -Direct the commit goes to a release branch with a pull request against main; the first
  publish, when the public repository is empty, always goes straight to main. The published private commit is
  tagged published/<date>.

.EXAMPLE
  ./tools/publish.ps1 -Message "feat: dynamic launcher lists" -Body "Lists, paging, dedicated decks."
#>
param(
    [Parameter(Mandatory)][string]$Message,
    [string]$Body = "",
    [string]$Ref = "HEAD",
    [string]$Remote = "public",
    [string]$Branch = "",
    [switch]$Direct
)

$ErrorActionPreference = "Stop"

function Invoke-Git { $out = & git.exe @args; if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed" }; $out }

if ($Message.Length -gt 50) { Write-Warning "subject is $($Message.Length) characters; keep it at 50 or fewer" }
if (Invoke-Git status --porcelain) { throw "working tree has uncommitted changes" }

$name = (& git.exe config publish.name) ?? (Invoke-Git config user.name)
$email = & git.exe config publish.email
if (-not $email) { throw "set the public author email first: git config publish.email <address>" }

Invoke-Git fetch $Remote | Out-Null
$parent = & git.exe rev-parse --verify -q "refs/remotes/$Remote/main"
$tree = Invoke-Git rev-parse "$Ref^{tree}"
$excluded = (& git.exe show "${Ref}:.publishignore" 2>$null) | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith("#") }
if ($excluded) {
    # Build the filtered tree in a throwaway index so the working tree and the real index stay untouched.
    $index = New-TemporaryFile
    $env:GIT_INDEX_FILE = $index.FullName
    try {
        Invoke-Git read-tree $tree
        Invoke-Git rm --cached -r -q --ignore-unmatch -- @excluded | Out-Null
        $tree = Invoke-Git write-tree
    }
    finally {
        Remove-Item Env:GIT_INDEX_FILE
        Remove-Item $index.FullName -Force -ErrorAction SilentlyContinue
    }
}
if ($parent -and (Invoke-Git rev-parse "$parent^{tree}") -eq $tree) {
    Write-Host "public main already matches $Ref (minus .publishignore); nothing to publish"
    return
}

$env:GIT_AUTHOR_NAME = $env:GIT_COMMITTER_NAME = $name
$env:GIT_AUTHOR_EMAIL = $env:GIT_COMMITTER_EMAIL = $email
try {
    $commitArgs = @($tree) + ($parent ? @("-p", $parent) : @()) + @("-m", $Message) + ($Body ? @("-m", $Body) : @())
    $commit = Invoke-Git commit-tree @commitArgs
}
finally {
    Remove-Item Env:GIT_AUTHOR_NAME, Env:GIT_COMMITTER_NAME, Env:GIT_AUTHOR_EMAIL, Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue
}

if ($Direct -or -not $parent) {
    Invoke-Git push $Remote "${commit}:refs/heads/main"
    Write-Host "published $commit to $Remote/main"
}
else {
    if (-not $Branch) { $Branch = "release/" + (Get-Date -Format "yyyyMMdd-HHmm") }
    Invoke-Git push $Remote "${commit}:refs/heads/$Branch"
    $repo = (Invoke-Git remote get-url $Remote) -replace '^.*github\.com[:/]', '' -replace '\.git$', ''
    gh pr create --repo $repo --base main --head $Branch --title $Message --body ($Body ? $Body : $Message)
    if ($LASTEXITCODE -ne 0) { throw "pull request creation failed; branch $Branch is pushed" }
}

$tag = "published/" + (Get-Date -Format "yyyyMMdd-HHmm")
Invoke-Git tag $tag (Invoke-Git rev-parse $Ref)
Write-Host "tagged $Ref as $tag"
