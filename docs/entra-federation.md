# Entra ID-federatie (taak #1482, Klantmachine-epic)

Klantmedewerkers (bv. Perridon) loggen met hun eigen Microsoft-bedrijfsaccount
in op de workspace-portal, zonder apart IAM-wachtwoord.

## Hoe de flow loopt

1. Workspace-portal → `GET /connect/authorize` (OpenIddict) → geen `IAM.Session`
   → redirect naar de loginpagina.
2. Loginpagina toont naast wachtwoord/magic-link/SMS ook de actieve externe
   providers (publieke lijst: `GET /api/auth/social/providers`).
3. Knop "Sign in with Microsoft" → `GET /api/auth/social/{providerId}/start?returnUrl=…`
   → 302 naar het Entra-authorize-endpoint van de klant-tenant (per-tenant
   gepind via de MetadataUrl van de provider).
4. Entra → `GET /api/auth/social/callback?code&state`. De state is een
   DataProtection-versleuteld pakketje (providerId + returnUrl + 10-min-expiry),
   dus niet te vervalsen. Code-exchange → id_token-claims → bestaande
   ExternalLogin-koppeling of e-mail-match of auto-create (per provider
   configureerbaar) → **SignIn `IAM.Session`** → terug naar het opgeschorte
   `/connect/authorize`, dat de gewone OIDC-flow afmaakt.
5. De workspace-router mapt de e-mailclaim uit het IAM-id_token op de
   tenant-registry (workspace-map.json in jengo-cloud-workspace) en de gebruiker
   landt in zíjn workspace.

**E-mail → tenant-registry:** het e-mailadres dat Entra levert (claim `email`,
terugval `preferred_username`/UPN) moet exact voorkomen in de tenant-registry
(of als alias). Wijkt de UPN af van het geregistreerde adres, voeg hem dan als
alias toe aan de gebruiker in tenants.json.

## Provider aanmaken (per klant-tenant)

`POST /api/identity-providers` (admin):

```json
{
  "name": "entra-perridon",
  "displayName": "Sign in with Microsoft",
  "type": "Microsoft",
  "tenantId": "<IAM-tenant-guid van de klant, of null>",
  "clientId": "<Application (client) ID uit de app-registratie>",
  "clientSecret": "<client secret>",
  "metadataUrl": "https://login.microsoftonline.com/<entra-tenant-id>/v2.0/.well-known/openid-configuration",
  "autoCreateUsers": false,
  "isActive": true
}
```

- **MetadataUrl gezet** → endpoints via OIDC-discovery, gepind op de
  Entra-tenant van de klant (accounts van andere tenants worden door Entra zelf
  geweigerd). Zonder MetadataUrl valt type Microsoft terug op `/common`
  (multi-tenant) — voor klantfederatie altijd pinnen.
- `autoCreateUsers: false` = alleen vooraf aangemaakte IAM-gebruikers (op
  e-mail gematcht) kunnen binnenkomen — aanbevolen voor klantmachines
  (fail closed). Op `true` met `defaultRoleId` voor open onboarding.
- Type `OIDC` werkt identiek voor elke andere idp met een discovery-URL.

## Wat de klant-IT eenmalig doet (admin-consent)

1. **App-registratie** in hun Entra-tenant (Entra admin center → App
   registrations → New registration):
   - Naam: bv. "Jengo Workspace Login"
   - Supported account types: *Accounts in this organizational directory only*
     (single-tenant)
   - Redirect URI (Web): `https://maendeleo.martiendejong.nl/auth/api/auth/social/callback`
     (of de `SocialAuth:BrowserCallbackUri` van de betreffende installatie)
2. **Client secret** aanmaken (Certificates & secrets) en de vervaldatum
   agenderen.
3. **API permissions**: alleen delegated `openid`, `email`, `profile`
   (Microsoft Graph). Geen applicatierechten, geen `User.Read` nodig — het
   profiel komt uit het id_token. → *Grant admin consent* klikken zodat
   gebruikers geen individuele consent-prompt krijgen.
4. **Optional claims**: op het id_token de claim `email` aanzetten (Token
   configuration → Add optional claim → ID → email). Zonder die claim wordt de
   UPN (`preferred_username`) gebruikt.
5. Aanleveren aan ons: **Application (client) ID**, **client secret** (via de
   vault, niet per mail) en **Directory (tenant) ID**.

## Configuratie aan onze kant

- `SocialAuth:BrowserCallbackUri` (optioneel): absolute callback-URL als die
  afwijkt van `{Jwt:Issuer}/api/auth/social/callback`.
- De loginpagina toont automatisch elke actieve provider; er is niets extra's
  te deployen per klant behalve de provider-rij.

## Test (done-when van de taak)

Testgebruiker met Microsoft-account → workspace-portal openen → "Sign in with
Microsoft" → Entra-login (incl. MFA van de klant) → landt in de eigen
workspace. Vereist een echte app-registratie in een Entra-tenant; de
IAM-kant is volledig config-gedreven (provider-rij hierboven).
