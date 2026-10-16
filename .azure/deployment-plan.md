# Live Context Nonproduction Deployment Plan

## Status

Ready for Validation

## 1. Deployment intent

Deploy the validated ACS live-caption participant and live-context Q&A changes
to the existing nonproduction Teams meeting bot so a tester can:

1. Schedule and start a Teams meeting.
2. Open the ACS participant page.
3. Join that participant to the meeting and admit it from the lobby.
4. Speak during the meeting.
5. Ask the existing Teams agent a question about what was just said.
6. Receive an answer grounded in the finalized captions captured so far.

This is a controlled POC validation, not a production release.

## 2. Confirmed existing environment

| Item | Value |
|---|---|
| Source repository | `ortizjorge639/bot-meetings-dotnet` |
| Deployment branch | `main` |
| GitHub workflow | `.github/workflows/deploy-main.yml` |
| GitHub environment | `production` (existing name; workload remains nonproduction) |
| App Service | `bot-meeting-plan` |
| Resource group | `bot-meeting-rg` |
| Region | `centralus` |
| URL | `https://bot-meeting-plan-d3htfcbtevfphuf3.centralus-01.azurewebsites.net` |
| Authentication | GitHub OIDC secrets already used by successful deployments |
| Existing application | Teams bot, Graph post-meeting transcript ingestion, `gpt-4.1-mini` Q&A |

The current local Azure CLI subscription does not contain this resource group.
Deployment must use the existing GitHub OIDC identity to avoid creating a
duplicate environment in the wrong subscription.

## 3. Change scope

### Application

- Deploy the validated .NET live-context endpoints and state handling.
- Build and publish the ACS Calling SDK browser participant under
  `/acs-participant/`.
- Preserve existing Graph transcript ingestion and post-meeting Q&A.

### Azure

- Idempotently create one Azure Communication Services resource in
  `bot-meeting-rg`.
- Use ACS data location `United States`.
- Set these App Service settings without printing their values:
  - `LiveContext__Enabled=true`
  - `LiveContext__AccessKey=<GitHub secret>`
  - `LiveContext__AcsConnectionString=<ACS connection string>`
  - `LiveContext__DataPath=/home/data/bot-meetings/live-context`

### Secret handling

- Generate a random access key of at least 32 characters.
- Store it as the GitHub Actions secret `LIVE_CONTEXT_ACCESS_KEY`.
- Copy it to the tester's local clipboard so it can be pasted into the ACS
  participant page without writing it to source, logs, or documentation.
- Keep the ACS connection string only in the workflow process and App Service
  configuration.

## 4. Deployment method

Recipe type: `cicd-azure-cli`

1. Extend the existing GitHub workflow after Azure login to:
   - install/use the Azure Communication CLI extension;
   - create or reuse the ACS resource;
   - retrieve its connection string without logging it;
   - update the four App Service settings above.
2. Commit the validated implementation and deployment-plan changes.
3. Push the commit to `standalone/main`.
4. Let the protected GitHub Actions workflow test, build, configure Azure, and
   deploy the App Service.
5. Do not deploy through the currently signed-in local Azure subscription.

## 5. Pre-deployment validation

Already completed locally:

- 37 .NET tests passed.
- 9 ACS browser tests passed.
- TypeScript typecheck passed.
- Browser production build passed.
- Bicep validation passed.
- Provisioning PowerShell syntax passed.
- Published package contains `/acs-participant/`.
- Independent review completed with no remaining high-confidence defects.

Required before execution:

- Run `azure-validate` against this plan.
- Confirm the existing GitHub deployment identity can create Communication
  Services resources and update App Service settings in `bot-meeting-rg`.
- Confirm the tester accepts `United States` as the ACS data location.

All validation checks pass:

- [ ] Core validation: Azure CLI, authentication, build, target validation, and
  what-if or an equivalent protected-workflow preflight.
- [ ] Docker build: not applicable; this deployment uses App Service package
  deployment.
- [ ] Azure Policy validation for Communication Services and App Service
  configuration.
- [ ] GitHub OIDC target verification because the target subscription is not
  available to the current local Azure CLI identity.
- [ ] Application and browser test suites.
- [ ] Deployment workflow syntax and required-secret checks.

Approved on October 16, 2026:

- Nonproduction deployment plan approved.
- ACS data location approved as `United States`.
- `LIVE_CONTEXT_ACCESS_KEY` generated, saved as a GitHub Actions secret, and
  copied to the tester's local clipboard without being logged.
- Workflow YAML parsed successfully. A dedicated `actionlint` executable was
  unavailable, so GitHub-specific behavior remains part of `azure-validate`
  and the protected workflow run.

## 6. Post-deployment verification

The workflow and operator will verify:

1. `/meeting` returns the existing bot page.
2. `/acs-participant/` returns the ACS participant page.
3. `/health/live` and `/health/ready` return healthy.
4. `/version` contains the deployed commit.
5. `/api/messages` preserves its expected method behavior.
6. `/api/acs/token` returns `401` without the POC access key.
7. `/api/acs/token` issues a valid short-lived ACS identity token with the key.
8. The existing Teams bot still responds in meeting chat.

## 7. Live Teams proof

1. Schedule a normal private Teams meeting and add the existing bot/app to its
   meeting chat before the meeting starts.
2. Invite at least one additional Teams participant who can speak and admit the
   ACS participant.
3. Start the meeting and wait for the bot's meeting-start message.
4. Open the deployed `/acs-participant/` page in current Edge or Chrome.
5. Paste the Teams meeting join link, choose a display name such as
   `Live Context Test`, and paste the access key from the local clipboard.
6. Select **Join meeting**.
7. Admit `Live Context Test` from the Teams lobby.
8. Confirm the page reports `Connected`, captions active, and finalized
   captions delivered.
9. Say a unique fact, for example: “The validation code is cedar seven.”
10. Ask the agent in the meeting chat: “What is the validation code?”
11. Verify the answer uses the newly spoken fact and includes a live-caption
    source.
12. Record caption receipt, persistence, question, and answer timestamps.

All participants must be notified before captions are enabled and stored.

## 8. Rollback

If configuration or live testing fails:

1. Set `LiveContext__Enabled=false` on the existing App Service.
2. Redeploy the previously successful commit
   `2a7f6f1ecb998b28dc6ea24543c0b577f8ffa9df`.
3. Verify `/meeting`, health endpoints, `/api/messages`, Graph transcript
   ingestion, and completed-transcript Q&A.
4. Leave the ACS resource in place until diagnosis is complete; deletion is a
   separate destructive action requiring approval.

This rollback preserves the existing V1 behavior.

## 9. Validation Proof

Pending `azure-validate`. Only that workflow may mark this plan as Validated.

Preparation evidence:

| Check | Result |
|---|---|
| `.NET` tests | 37 passed |
| ACS browser tests | 9 passed |
| TypeScript typecheck | Passed |
| Browser production build | Passed |
| Bicep build | Passed |
| Provisioning PowerShell parse | Passed |
| Publish package contains ACS participant | Passed |
| Independent code review | No remaining high-confidence findings |
| Deployment workflow YAML parse | Passed |
| Required GitHub POC access-key secret | Configured |
