# GitHub setup

## Option A - GitHub CLI

From the repository folder:

```powershell
.\scripts\publish-github.ps1 -Visibility private
```

When you are ready to make it public:

```powershell
gh repo edit --visibility public
```

Or create it public from the beginning:

```powershell
.\scripts\publish-github.ps1 -Visibility public
```

The script requires Git and GitHub CLI (`gh`) already installed and authenticated.

## Option B - GitHub web UI

1. Create an empty repository named `VictusFanControl`.
2. Do not add a README, license or `.gitignore`; they already exist locally.
3. Then run:

```powershell
git init -b main
git add .
git commit -m "Initial telemetry-only scaffold"
git remote add origin https://github.com/YOUR-USER/VictusFanControl.git
git push -u origin main
```

## After the first push

Create the initial issues listed in `ISSUES_TO_CREATE.md`, then begin with issue #1: baseline telemetry validation.
