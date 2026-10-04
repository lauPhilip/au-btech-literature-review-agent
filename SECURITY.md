# Security policy

## Reporting a vulnerability

Please report security problems privately through GitHub: open the repository's **Security** tab and choose **Report a vulnerability** (https://github.com/lauPhilip/au-btech-literature-review-agent/security/advisories/new). Do not open a public issue for a vulnerability.

Include what you found, how to reproduce it, and what an attacker could do with it. We aim to answer within a week. When the problem is fixed, the advisory is published, and you are credited if you want to be.

The same address is in [`/.well-known/security.txt`](https://au-btech-literature-review-agent.dk/.well-known/security.txt) on the public site.

## Supported versions

Only the latest version on the `master` branch, which is what runs at https://au-btech-literature-review-agent.dk, receives security fixes.

## What is in scope

- The web app: the dashboard, Review Output, Metrics and the other pages, and the download endpoints under `/api/`.
- Handling of API keys entered in the browser, the run quota, and access to other people's runs.
- Prompt injection that makes the app do something other than describe it (text from papers or from the review form steering the model is expected to be flagged and recorded, not prevented; see the wiki).

The third-party services the app calls (the model provider and the scholarly databases) are out of scope; report problems with them to their owners.

## How the app is protected

The security measures (Content-Security-Policy, input checks, quota, key handling, run isolation, path checks) are described in [the developer wiki](docs/wiki/06-web-app-and-operations.md#security-measures).
