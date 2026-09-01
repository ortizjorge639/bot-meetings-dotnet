# Publish and validate in your organization

## Prepare the package

1. Obtain stable Teams app and bot app IDs.
2. Host real privacy, terms-of-use, and developer pages on public HTTPS URLs.
3. Run the package builder with the deployed App Service URL.
4. Open the package in Teams Developer Portal and run **App validation**.
5. Review every requested permission with security and tenant administrators.

The source manifest is a template and intentionally contains replacement markers. Do not upload it directly. The generated ZIP must contain `manifest.json`, `color.png`, and `outline.png` at the archive root.

## Pilot installation

For a small test group, allow custom app upload with an app setup policy and restrict app access with app-centric management or an app permission policy. Upload the generated ZIP from **Apps** > **Manage your apps** > **Upload an app**.

## Publish to the organization catalog

Choose one path:

- In Teams Developer Portal, open the app and select **Publish** > **Publish to org**. A Teams administrator reviews the pending submission in Teams admin center.
- A Teams administrator uploads the ZIP directly in **Teams apps** > **Manage apps**. Admin upload makes it available in the organization catalog without the developer approval queue.

After publication:

1. Set the app to Allowed.
2. Assign access to the pilot population with app-centric management or an app permission policy.
3. Optionally pin or install it with an app setup policy.
4. Allow policy propagation before testing.

When publishing an update, keep the Teams app ID and bot ID unchanged and increment the manifest version. Existing policies continue to apply to the updated app.

## End-to-end acceptance matrix

| Test | Expected result |
| --- | --- |
| Health probes | `/health/live`, `/health/ready`, and `/version` return success. |
| Scheduled private meeting | Bot receives start/end events after being installed in the meeting chat. |
| Participant events | Join/leave handlers tolerate missing member data. |
| Transcript publication delay | Worker retries and eventually posts a readiness notice. |
| Speaker attribution | At least two known speakers appear correctly in sources. |
| Supported answer | Answer includes valid source labels, speaker, and timestamp. |
| Unsupported answer | Bot refuses or reports no relevant transcript material. |
| Prompt injection in transcript | Transcript instructions are ignored as data. |
| Cross-chat isolation | Another meeting chat cannot retrieve this transcript. |
| Cross-tenant request | Activity is rejected and no transcript/model operation occurs. |
| Oversized question | Request is rejected before model invocation. |
| Saturated model | Extra request gets a bounded busy response. |
| Expired data | Job and associated document disappear after retention cleanup. |
| App catalog install | Published app behaves the same as the pilot package. |

Repeat this matrix after changing the manifest, Graph permissions, Teams policies, bot identity, model deployment, or hosting topology.

## Official references

- [Manage apps in Developer Portal](https://learn.microsoft.com/microsoftteams/platform/concepts/build-and-test/manage-your-apps-in-developer-portal)
- [Manage custom apps in Teams admin center](https://learn.microsoft.com/microsoftteams/teams-custom-app-policies-and-settings)
- [Upload an app in Teams](https://learn.microsoft.com/microsoftteams/platform/concepts/deploy-and-publish/apps-upload)
- [Manage access to Teams apps](https://learn.microsoft.com/microsoftteams/app-policies)
