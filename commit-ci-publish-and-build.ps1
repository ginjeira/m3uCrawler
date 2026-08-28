param(
    [string]$CommitMessage = "CI: use runner publish and build image",
    [switch]$Push = $true
)

git add m3uCrawler/Dockerfile.publish .github/workflows/docker-ghcr.yml m3uCrawler/Dockerfile
git commit -m $CommitMessage || Write-Host "No changes to commit"
if ($Push) { git push origin main }
