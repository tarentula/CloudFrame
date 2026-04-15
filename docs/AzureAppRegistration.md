# Azure App Registration Setup for CloudFrame

CloudFrame uses MSAL.NET to authenticate with personal Microsoft accounts.
You need to register a free Azure AD application once. It takes about 5 minutes.

## Steps

### 1. Create the app registration

1. Go to https://portal.azure.com and sign in with **any** Microsoft account
   (it doesn't have to be the same account whose OneDrive you'll show).
2. Search for **"App registrations"** and click **New registration**.
3. Fill in:
   - **Name**: CloudFrame
   - **Supported account types**: *Personal Microsoft accounts only*
   - **Redirect URI**: select **Public client/native (mobile & desktop)**
     and enter `http://localhost`
4. Click **Register**.

### 2. Copy the Client ID

On the app's **Overview** page, copy the **Application (client) ID** — a GUID
that looks like `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.

Open `src/CloudFrame.Providers.OneDrive/MsalAuthManager.cs` and replace:

```csharp
private const string ClientId = "YOUR_CLIENT_ID_HERE";
```

with your actual Client ID.

### 3. Verify API permissions (should be automatic)

Under **API permissions**, confirm you see:
- `Microsoft Graph → Files.Read` (Delegated)
- `Microsoft Graph → offline_access` (Delegated)

These are added automatically for personal account apps. If missing, click
**Add a permission → Microsoft Graph → Delegated → Files.Read** and repeat
for `offline_access`.

You do **not** need to click "Grant admin consent" — these are user-delegated
permissions that each user grants at first sign-in.

### 4. No client secret needed

CloudFrame is a public client (desktop app). No secret or certificate is
required — MSAL handles the PKCE flow automatically.

## What happens at runtime

- **First launch**: a browser window opens for the user to sign in to their
  Microsoft account. They grant CloudFrame permission to read their files.
- **Subsequent launches**: MSAL silently refreshes the token from the
  DPAPI-encrypted cache on disk — no browser, no prompt, < 50 ms.
- **Token expiry**: Microsoft refresh tokens for personal accounts expire after
  90 days of inactivity. If this happens, CloudFrame will show a prompt in
  the tray icon to re-authenticate.

## Privacy

- CloudFrame only requests `Files.Read` — read-only access to the user's files.
- No data is sent anywhere except Microsoft's Graph API endpoints.
- The token cache is encrypted with DPAPI (Windows Data Protection API) and
  stored in `%LOCALAPPDATA%\CloudFrame\` — only the current Windows user can
  read it.
