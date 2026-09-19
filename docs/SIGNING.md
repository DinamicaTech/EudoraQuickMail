# Release signing

Eudora QuickMail releases must be signed with an identity controlled by DinamicaTech. The
upstream project's certificate, Azure account and signing metadata are deliberately not part of
this fork.

## Current state

Version 0.8.71 is intended to be published as a GitHub **pre-release** without a signature. It is
a bootstrap build for public review and for completing the SignPath Foundation application. Do
not promote it to a stable/latest release while it remains unsigned.

## SignPath Foundation onboarding

1. Make the source repository and the initial unsigned pre-release public.
2. Apply to SignPath Foundation for the `DinamicaTech/QuickMail` project.
3. Configure the SignPath organization, project, signing policy and artifact configuration using
   repository or environment variables/secrets; never commit IDs, API tokens or certificates.
4. Add the official SignPath GitHub action between packaging and the Release step. Sign both x64
   and ARM64 portable executables and installers, then upload only the returned signed artifacts.
5. Verify Authenticode signatures and their timestamp in CI before creating the GitHub Release.
6. Publish one more pre-release, test installation and update on clean Windows x64 and ARM64
   systems, and only then promote a signed release to stable/latest.

The packaging jobs deliberately remain split by architecture and the Release step consumes files
from explicit paths. This provides clean insertion points for signing without changing versioning,
Velopack feeds or release naming.

Self-signed certificates are not acceptable for public distribution.
