# Security policy

PdfPecker parses input that may come from untrusted sources: HTML, CSS, SVG, Markdown, fonts, images and PDF files.
A crash, hang, runaway memory use or unexpected exception type on crafted input is a security bug, as is anything that
lets a document read files or fetch URLs the caller did not allow.

## Supported versions

Security fixes go into the latest release on [nuget.org](https://www.nuget.org/packages/PdfPecker). Please upgrade to
it before reporting; older versions do not get patch releases.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through GitHub instead:
[Report a vulnerability](https://github.com/Moonwalker1307/PdfPecker/security/advisories/new) (the **Security** tab,
then **Report a vulnerability**).

Include the PdfPecker version, the .NET runtime, and the smallest input that shows the problem, attached as a file.
Inputs found with the fuzzer in `fuzz/PdfPecker.Fuzz` are welcome as they are.

You can expect an acknowledgement within a few days. Once a fix is released, the advisory is published and you are
credited unless you prefer not to be.

## Out of scope

- Files read while `PdfOptions.AllowFileAccess` is on, and whatever a caller's `PdfOptions.ResourceResolver`
  returns. Both hand the document access the caller chose to give it. (A document reading a file outside
  `BaseDirectory` while `AllowFileAccess` is off is in scope.) The converter never touches the network itself, so
  deciding which URLs to fetch is the resolver's job.
- JavaScript: PdfPecker does not run scripts, so script-based attacks on the converter do not apply.
