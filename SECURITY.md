# Security Policy

## Supported versions

STMSharp is under active development toward a stable 3.0.0 release. During the preview phase, security fixes are applied to the latest published preview and to the `main` branch. Once a stable version is released, this policy will be updated to state the supported version range.

## Reporting a vulnerability

If you believe you have found a security vulnerability in STMSharp, please report it privately rather than opening a public issue, so that a fix can be prepared before the details are disclosed.

Report a vulnerability by using GitHub's private vulnerability reporting on the repository, or by contacting the maintainer at francesco.delre[at]protonmail.com. Please include a description of the vulnerability, the affected version or commit, and a reproduction if you have one.

You can expect an acknowledgement of your report. Once the report is assessed, the maintainer will work on a fix and coordinate a disclosure timeline with you.

## Scope

STMSharp is a concurrency library. The most relevant class of defect for this project is a correctness failure under concurrency that could be exploited to corrupt shared state or to violate the documented isolation guarantees. Reports that demonstrate such a failure are in scope and are treated with the same seriousness as a conventional vulnerability.
