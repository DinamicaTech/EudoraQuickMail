# Release signing

Free code signing provided by [SignPath.io](https://signpath.io/), certificate by
[SignPath Foundation](https://signpath.org/).

Eudora QuickMail releases are intended to be signed through the SignPath Foundation open-source
program. The certificate is issued in SignPath Foundation's name; it is not owned by DinamicaTech.
The upstream project's certificate, Azure account and signing metadata are deliberately not part
of this fork.

## Team roles

- **Committers and reviewers:** members authorized to maintain the
  [`DinamicaTech/QuickMail`](https://github.com/DinamicaTech/QuickMail) repository.
- **Approvers:** [owners of the DinamicaTech GitHub organization](https://github.com/orgs/DinamicaTech/people?query=role%3Aowner).

All maintainers must use multi-factor authentication for GitHub and SignPath. Contributions from
people without direct commit access must be reviewed before they are merged. Every signing request
requires manual approval by an approver.

## Privacy policy

QuickMail's data-handling and network behavior are described in the
[Dinámica Ingeniería privacy policy](https://www.dinamica.tech/privacy). QuickMail connects only
to mail, calendar, contact, update and support services selected or invoked by the user; it does
not require a Dinámica Ingeniería cloud account.

## Current state

Version 0.8.72 is published as a GitHub **pre-release** without a signature. It is a bootstrap
build for public review and for completing the SignPath Foundation application. Do not promote it
to a stable/latest release while it remains unsigned.

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
