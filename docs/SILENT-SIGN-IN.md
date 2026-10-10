# Silent sign-in: `prompt=none` and the IAM session cookie

Since task 5053 an app can renew its IAM sign-in without showing IAM UI, and the IAM session is
recognised when the app lives on another domain (for example `knowledge.prospergenics.com` sending
the user to `maendeleo.martiendejong.nl/auth/`).

## What an app sends

A normal authorization-code + PKCE request to `/connect/authorize`, plus `prompt=none`:

```
GET /connect/authorize?client_id=jengo-knowledge&response_type=code&redirect_uri=...
    &scope=openid profile email&state=...&code_challenge=...&code_challenge_method=S256&prompt=none
```

Send it as a top-level browser navigation (a redirect or a link), not as a hidden iframe or a
fetch/XHR. The app then gets exactly one of these answers, always as a redirect to its own
`redirect_uri` with the original `state`. IAM never shows its login page or an HTML page for a
`prompt=none` request.

| Situation | Answer to the app |
|---|---|
| IAM session present, user active, user may use the app | `?code=...&state=...` (a normal sign-in, no IAM UI) |
| No IAM session (never signed in, expired, browser closed) | `?error=login_required&state=...` |
| IAM session belongs to a deactivated or deleted account | `?error=login_required&state=...` |
| User has no role for the app (the app has a role catalog and the user holds none of its roles) | `?error=access_denied&state=...` |

On `login_required` the app falls back to its own screen, or starts the interactive flow (the same
request without `prompt=none`). On `access_denied` retrying interactively only shows the "No access"
page, so the app should show its own "you have no access" message.

Without `prompt=none` nothing changed: no session redirects to `/auth/login?returnUrl=...`, an
inactive user gets HTTP 400, and a missing app role gets the HTML "No access" page.

`interaction_required` is never returned: IAM has no consent screen and no other step a signed-in
user would still have to complete, so there is nothing that triggers it.

## The session cookie

| Cookie | SameSite | Why |
|---|---|---|
| `IAM.Session` | **Lax** | `/connect/authorize` is the only reader. An app on another domain sends the user there with a cross-site top-level navigation, and a `Strict` cookie is not sent on that, so IAM would never see the session and `prompt=none` could only ever answer `login_required`. `Lax` is sent on a top-level `GET` navigation and withheld from cross-site sub-requests (fetch, XHR, iframes, image loads). `HttpOnly` and `Secure` are unchanged. |
| `refreshToken` | Strict | Unchanged. Only IAM's own `/api/auth/refresh` and `/api/auth/logout` read it; it is never needed on a cross-site sign-in. |

Limits worth knowing:

- A `Lax` cookie is not sent on a cross-site form `POST`. An app that POSTs the authorize request
  from another domain would not carry the session, so apps should send the request as a `GET`.
- Only a password login (and its 2FA step) creates `IAM.Session` today (read from the code,
  `AuthController.CompleteLoginAsync` is the only place that signs in to it). A user who signed in
  to IAM with a magic link, SMS/e-mail code, passkey or social login has no session cookie, so for
  them `prompt=none` answers `login_required`. Making those logins create the session is a separate
  task.
- `prompt=login` and `max_age` are still ignored.
- The session lasts 8 hours idle (sliding). It ends sooner when the browser closes unless the user
  ticked **Remember me** (then 30 days).

## Remember me default (decision for task 5053)

Question put to Martien: should a sign-in that starts from an app remember the user for 30 days by
default?

- **A - leave as is (Remember me stays unticked).** No security change; silent renewal works while
  the browser stays open, within the 8 hour idle window.
- B - pre-tick Remember me when the sign-in starts from an app (user can untick). 30-day session,
  a one-line change in the login page, riskier on shared computers because that session also opens
  the Password Manager.
- C - always 30 days for first-party apps, no checkbox. Widest exposure; not recommended.

**Shipped: A**, because no reply came before this task was implemented (the task states "with no
reply the task ships A"). Jengo Knowledge keeps its own 30 day session, so the IAM session only
matters when that session ends early. If people still land on the IAM form after restarting the
browser, B is the follow-up: pre-tick the checkbox on `/auth/login` when `returnUrl` points at
`/connect/authorize`.

## Testing

- `dotnet test tests/IAM.API.Tests --filter PromptNone` drives the real login -> `/connect/authorize`
  pipeline in-process.
- After a deploy (develop is not live yet, see task 5000):
  `curl -sI 'https://maendeleo.martiendejong.nl/auth/connect/authorize?client_id=jengo-knowledge&response_type=code&redirect_uri=<registered>&scope=openid&state=x&code_challenge=<S256>&code_challenge_method=S256&prompt=none'`
  must answer `302` to the `redirect_uri` with `error=login_required&state=x`.
- Cross-domain cookie delivery (a signed-in browser redirected from another domain is recognised)
  can only be checked by hand in a browser.
