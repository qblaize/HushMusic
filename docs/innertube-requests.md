# InnerTube request spec for HushMusic (derived from ytmusicapi)

Research date: **2026-10-07**. This document describes the **request side** of the YouTube Music InnerTube API exactly as `sigma67/ytmusicapi` implements it, plus what was verified live and what the issue trackers say. No C# here.

## Sources and legend

| Source | Version / date |
|---|---|
| ytmusicapi source | `master` @ `4aeaf7d` (2026-10-01). Latest release `1.12.3` (2026-09-16). File paths below are relative to `ytmusicapi/` in that repo. |
| ytmusicapi tests | `tests/conftest.py`, `tests/mixins/*.py` (same commit) |
| GitHub issues / PRs | sigma67/ytmusicapi and yt-dlp/yt-dlp, read 2026-10-07 via `gh` |
| Live probes | Anonymous requests to `music.youtube.com` on 2026-10-07 (no account, no cookies except `SOCS=CAI`) |
| yt-dlp runs | `yt-dlp 2026.03.03` (pip install), metadata only (`-j` / `-F`) |

Tags used in this document:

- **[ytm]**: literally what ytmusicapi does (Hush follows it as the reference implementation).
- **[live]**: verified by an anonymous probe on 2026-10-07.
- **[issue #N]**: reported in a GitHub issue, not verified by us.
- **[not-ytm]**: does not come from ytmusicapi. Use only as a documented fallback.
- **[uncertain]**: our best understanding. Verify before relying on it.

---

## TL;DR: what this changes for the project

1. **OAuth device flow does not work against InnerTube today.** Every authenticated InnerTube call made with a Bearer token fails with `HTTP 400 "Request contains an invalid argument."`. This started on 2025-08-29. Issue #813 is still OPEN and the maintainer has said there is no path forward. ytmusicapi's own test suite now runs its "oauth" tests with browser cookies. **Cookie (browser) auth is the only working authenticated mode**, so Hush signs in with cookies only. See section 5.
2. ytmusicapi sends `?alt=json`, plus `&key=<public web key>` in cookie mode, not `?prettyPrint=false`. All variants are accepted **[live]**. Adding `prettyPrint=false` cuts response size by 60-70% **[live]** and is harmless. See section 1.
3. `clientVersion` is computed from the date: `"1." + UTC yyyyMMdd + ".01.00"`. See section 2.
4. **yt-dlp 2026.03.03 cannot produce a playable stream.** `-f bestaudio` fails outright. With `--js-runtimes node` it is still offered only itag 18 (360p muxed MP4), and that URL returns **403**. A newer yt-dlp (at least `2026.08.19`, or nightly) plus a JS runtime is required; Hush installs and updates the official build itself. See section 8.
5. `add_history_item` needs the **`player`** endpoint plus a GET to **`s.youtube.com/api/stats/playback`**. See section 6.10.

---

## 0. The request pipeline in ytmusicapi

Every InnerTube call goes through `ytmusic.py:YTMusicBase._send_request(endpoint, body, additionalParams="")` **[ytm]**:

1. `body.update(self.context)`: the context object is merged into the body as a **top-level key `"context"`**, a sibling of the endpoint fields.
2. `POST YTM_BASE_API + endpoint + self.params + additionalParams`
   - `json=body` (serialized JSON; `Content-Type: application/json`)
   - `headers=self.headers` (section 3; the `authorization` header is recomputed on every request)
   - `cookies=self.cookies`, which is `{"SOCS": "CAI"}` (section 3.4)
   - Timeout 30 s (`_prepare_session`)
3. The response text is parsed as JSON. If `status_code >= 400`, ytmusicapi raises `YTMusicServerError("Server returned HTTP {code}: {reason}.\n" + response["error"]["message"])`.

Plain GETs (visitor-id bootstrap, album browseId lookup, history ping) go through `ytmusic.py:YTMusicBase._send_get_request(url, params, use_base_headers)`. It sends `headers = initialize_headers()` if `use_base_headers`, otherwise `self.headers`, and the same `SOCS` cookie.

---

## 1. Base URL and query parameters

**[ytm]** `constants.py`:

```
YTM_DOMAIN     = "https://music.youtube.com"
YTM_BASE_API   = YTM_DOMAIN + "/youtubei/v1/"
YTM_PARAMS     = "?alt=json"
YTM_PARAMS_KEY = "&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30"
```

`ytmusic.py:YTMusicBase.__init__` sets `self.params = YTM_PARAMS` and appends `YTM_PARAMS_KEY` **only when `auth_type == AuthType.BROWSER`** (cookie auth).

| Auth mode | URL ytmusicapi builds |
|---|---|
| Unauthenticated | `https://music.youtube.com/youtubei/v1/{endpoint}?alt=json` |
| OAuth (custom client) | `https://music.youtube.com/youtubei/v1/{endpoint}?alt=json` |
| Cookie / browser | `https://music.youtube.com/youtubei/v1/{endpoint}?alt=json&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30` |
| Any mode, query-string continuation | the above + `&ctoken={token}&continuation={token}` (section 7) |

`{endpoint}` can contain a slash, e.g. `music/get_search_suggestions`, `like/like`, `browse/edit_playlist`.

The key is the **public** web-client key embedded in the music.youtube.com page, not a secret. **[live]**: the `INNERTUBE_API_KEY` in today's page `ytcfg` is identical to the ytmusicapi constant.

### Does `?prettyPrint=false` matter?

**[live]** results, same body, unauthenticated:

| Query string | `music/get_search_suggestions` | `browse` FEmusic_home |
|---|---|---|
| `?alt=json` | 200, 32,525 bytes, pretty-printed | 200, 673,326 bytes |
| `?alt=json&key=...` | not tested | 200, 673,217 bytes |
| `?prettyPrint=false` | 200, 12,509 bytes, compact | not tested |
| `?alt=json&prettyPrint=false` | 200, 12,509 bytes | not tested |
| `?alt=json&prettyPrint=false&key=...` | not tested | 200, 201,295 bytes |
| *(no query string)* | 200, 32,525 bytes | not tested |

Conclusion: there is **no functional difference**, only bandwidth (70% less on the home page). Recommendation: keep ytmusicapi's parameters and add `prettyPrint=false`:
`?alt=json&prettyPrint=false`, with `&key=...` in cookie mode. Keep the key in config, not code.

---

## 2. Client context

**[ytm]** `helpers.py:initialize_context()` + `ytmusic.py:YTMusicBase.__init__`:

```python
{"context": {"client": {"clientName": "WEB_REMIX",
                        "clientVersion": "1." + time.strftime("%Y%m%d", time.gmtime()) + ".01.00"},
             "user": {}}}
```

Then, in `__init__`:

- `location` (optional, must be in `SUPPORTED_LOCATIONS`) → `context.client.gl = location`, e.g. `"US"`. **Not set by default**, so the server decides.
- `language` (default `"en"`, must be in `SUPPORTED_LANGUAGES`) → `context.client.hl = language`. Always set.
- `user` (optional brand-account id, 21 digits) → `context.user.onBehalfOfUser = user`.

**clientVersion format (confirmed):** `"1." + <current UTC date as yyyyMMdd> + ".01.00"`, for example `1.20261007.01.00` on 2026-10-07. ytmusicapi computes it **once, when the client object is constructed**, not per request.

- **[live]** The real web app currently reports `1.20261004.17.00`, from `ytcfg.INNERTUBE_CONTEXT.client.clientVersion`. The date-computed value was accepted for `browse`, `search`, `next` and `music/get_search_suggestions`.
- **[live]** The real web context has many more fields (`browserName`, `platform`, `userAgent`, `visitorData`, `osName`, ...). **ytmusicapi sends none of them**, and that works.
- `SUPPORTED_LANGUAGES` (`constants.py`): `ar cs de en es fr hi it ja ko nl pt ru tr ur zh_CN zh_TW`. These are the languages ytmusicapi has parser translations for. The server itself accepts more.
- `SUPPORTED_LOCATIONS`: 109 ISO country codes.

**Alternate context** **[ytm]** `ytmusic.py:YTMusicBase.as_mobile()` is used only by `get_lyrics(timestamps=True)`. It temporarily replaces `clientName`/`clientVersion` with `"ANDROID_MUSIC"` / `"7.21.50"`. `hl`, `gl`, `user` and **all headers stay unchanged**.

**Literal example body.** This is a `search` for songs, with `language="en"`, `location="US"`, no brand account, on 2026-10-07:

```json
{
  "query": "oasis wonderwall",
  "params": "EgWKAQIIAWoMEA4QChADEAQQCRAF",
  "context": {
    "client": {
      "clientName": "WEB_REMIX",
      "clientVersion": "1.20261007.01.00",
      "gl": "US",
      "hl": "en"
    },
    "user": {}
  }
}
```

With a brand account the user object becomes `"user": { "onBehalfOfUser": "101234161234936123473" }`.

---

## 3. Headers

### 3.1 Default headers (all modes)

**[ytm]** `helpers.py:initialize_headers()`. Literal values:

| Header | Value |
|---|---|
| `user-agent` | `Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0` (`constants.py:USER_AGENT`) |
| `accept` | `*/*` |
| `accept-encoding` | `gzip, deflate` |
| `content-type` | `application/json` |
| `content-encoding` | `gzip` (see note) |
| `origin` | `https://music.youtube.com` |

Plus, added lazily to the cached `base_headers`:

| Header | Value |
|---|---|
| `X-Goog-Visitor-Id` | visitor data string (section 3.3), or `""` if not found |

Notes:

- Header names are case-insensitive. ytmusicapi uses the casing shown above.
- **`content-encoding: gzip` is a quirk.** ytmusicapi sends this header but the body is **not** compressed (`requests` sends plain JSON). The server tolerates it. We recommend mirroring it for fidelity and **not** actually gzipping the body. Omitting it is probably also fine **[uncertain]**.
- `accept-encoding: gzip, deflate`: let the HTTP handler do automatic decompression.
- ytmusicapi does **not** send `X-YouTube-Client-Name` (67 for WEB_REMIX **[live]**) or `X-YouTube-Client-Version`. The real web app does. These headers are not needed.

### 3.2 Per-mode additions

| Mode | `ytmusicapi` AuthType | Headers sent (on top of / instead of 3.1) | Query string |
|---|---|---|---|
| Unauthenticated | `UNAUTHORIZED` | 3.1 only | `?alt=json` |
| OAuth | `OAUTH_CUSTOM_CLIENT` | 3.1 + `authorization: Bearer <access_token>` + `X-Goog-Request-Time: <unix seconds>` (both set per request in `YTMusicBase.headers`) | `?alt=json` |
| Cookie / browser | `BROWSER` | the user's stored headers (see below) + `authorization: SAPISIDHASH ...` recomputed per request (`YTMusicBase.headers` → `helpers.py:get_authorization`) | `?alt=json&key=...` |
| Full custom OAuth headers | `OAUTH_CUSTOM_FULL` | exactly the user-provided headers, untouched | `?alt=json` |

**Cookie-mode header set** **[ytm]** `auth/browser.py:setup_browser`:

1. Takes the headers copied from a real authenticated browser `POST /browse`.
2. Lower-cases them and drops `host`, `content-length`, `accept-encoding` and every header starting with `sec`.
3. **Requires** `cookie` and `x-goog-authuser`.
4. Overwrites with `initialize_headers()`, so ytmusicapi's user-agent, accept, content-type and origin win.

The documented minimal file (`docs/source/setup/headers_auth.json.example`) is: `Accept: */*`, `Authorization`, `Content-Type: application/json`, `X-Goog-AuthUser: 0`, `x-origin: https://music.youtube.com`, `Cookie`.

**Derived set for our WebView2 cookie login** (ytmusicapi's output for a minimal browser copy):

```
user-agent:        Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0
accept:            */*
accept-encoding:   gzip, deflate
content-type:      application/json
content-encoding:  gzip
origin:            https://music.youtube.com
x-origin:          https://music.youtube.com
x-goog-authuser:   0
cookie:            <all cookies for https://music.youtube.com, "name=value; name2=value2; ...">
X-Goog-Visitor-Id: <visitor data>
authorization:     SAPISIDHASH <ts>_<sha1hex>      (recomputed for every request)
```

`x-goog-authuser` is the index of the account inside the Google multi-login session, so `0` for the first or only account. The FAQ says a wrong index makes your library look empty.

Non-ASCII header values: `auth/auth_parse.py:parse_auth_str` percent-encodes any non-printable-ASCII characters in header values (`quote(value, safe=string.printable)`; fix for #856). .NET also rejects non-ASCII header values, so apply the same encoding.

### 3.3 Visitor id (`X-Goog-Visitor-Id`)

**[ytm]** `helpers.py:get_visitor_id`, called from `YTMusicBase.base_headers`. This happens once per client instance (a cached property) and **only if `X-Goog-Visitor-Id` is not already in the headers**:

1. `GET https://music.youtube.com` with **`initialize_headers()`** (no auth headers, even in cookie mode) and cookie `SOCS=CAI`.
2. Regex on the HTML: `ytcfg\.set\s*\(\s*({.+?})\s*\)\s*;`. Take the **first** match and parse it as JSON.
3. Read `VISITOR_DATA` and send it as header `X-Goog-Visitor-Id`. If nothing matches, the header is sent as an empty string.

**[live]**: the page contains 3 `ytcfg.set({...});` matches; the first one contains `VISITOR_DATA` (a base64-like string starting `Cgt...`). A request without the header also succeeded (`get_search_suggestions`, 200). The header matters mostly for personalisation and session continuity. Persist the visitor id and reuse it rather than fetching it on every start (our choice, not ytmusicapi).

### 3.4 Cookie header handling

**[ytm]** `YTMusicBase.__init__`: `self.cookies = {"SOCS": "CAI"}`. This is the "consent rejected" cookie; the comment points at yt-dlp. It is passed on every POST and GET.

Python `requests` merges cookies in a specific way (verified with ytmusicapi's own session):

- If the request already has an explicit `Cookie` header (cookie mode), **that header is sent unchanged** and `SOCS=CAI` (plus any session-jar cookies) is **not** added.
- Without an explicit `Cookie` header (unauthenticated, OAuth), the request sends `Cookie: SOCS=CAI` plus whatever the `requests.Session` jar collected from earlier responses. For example, the visitor-id GET sets `VISITOR_INFO1_LIVE`, `VISITOR_PRIVACY_METADATA`, `YSC`, `__Secure-BUCKET`, `__Secure-ROLLOUT_TOKEN`, `__Secure-YNID` **[live]**.
- Therefore, in cookie mode ytmusicapi **ignores `Set-Cookie` from responses**: the stored cookie string is never updated. This matters for session lifetime (section 4.4).

For our implementation:

- Unauthenticated: use a cookie container seeded with `SOCS=CAI`.
- Cookie mode: send the stored cookie string verbatim, make sure it contains `SOCS` if the browser had it, and decide consciously whether to apply `Set-Cookie` rotations. ytmusicapi does not.

---

## 4. Cookie / browser authentication

### 4.1 Which cookie, which origin

**[ytm]** `helpers.py:sapisid_from_cookie(raw_cookie)`:

1. Strips all `"` characters from the cookie string.
2. Parses it with `SimpleCookie`.
3. Returns the value of **`__Secure-3PAPISID`**. If that cookie is missing, `YTMusicBase.__init__` raises "Your cookie is missing the required value __Secure-3PAPISID".

Note that it is **not** the plain `SAPISID` cookie, although both usually have the same value.

**Origin** **[ytm]** `YTMusicBase.__init__`: `self.origin = base_headers.get("origin", base_headers.get("x-origin"))`, which is always **`https://music.youtube.com`** in practice.

### 4.2 SAPISIDHASH algorithm (`helpers.py:get_authorization`)

**[ytm]**, computed fresh for **every** request in `YTMusicBase.headers`:

```
ts     = current Unix time in whole seconds, as a decimal string   (str(int(time.time())))
input  = ts + " " + SAPISID + " " + ORIGIN                         (single spaces, UTF-8)
digest = lowercase hex of SHA-1(input)
header = "SAPISIDHASH " + ts + "_" + digest
```

The header is sent as `authorization: <header>`. `X-Goog-AuthUser` and `x-origin` come from the stored headers.

Test vector (computed with Python `hashlib`):

```
ts      = 1700000000
SAPISID = AbCdEfGhIjKlMnOp/QrStUvWxYz012345
ORIGIN  = https://music.youtube.com
input   = "1700000000 AbCdEfGhIjKlMnOp/QrStUvWxYz012345 https://music.youtube.com"
header  = SAPISIDHASH 1700000000_163e661c1ad4177128b563dfbe8b0ebfd1296a14
```

### 4.3 Newer variants (SAPISID1PHASH / SAPISID3PHASH / `_u`)

- **ytmusicapi does NOT implement them.** The current source has only the single `SAPISIDHASH` built from `__Secure-3PAPISID`.
- Browsers now send a three-part header: `SAPISIDHASH <ts>_<h> SAPISID1PHASH <ts>_<h> SAPISID3PHASH <ts>_<h>`, sometimes with a `_u` suffix on each part **[issue #781, 2025-06-29; #813 comment 2025-12-10]**.
- The single-part header **still works**. ytmusicapi's CI runs all authenticated tests with browser auth (`tests/conftest.py`: `yt_auth`, and `yt_oauth` is redirected to browser auth) and the `main` branch CI passed on 2026-10-01.
- **[not-ytm] fallback reference**, only if single-part starts returning 401. yt-dlp's `yt_dlp/extractor/youtube/_base.py:_get_sid_authorization_header` / `_make_sid_authorization`, read from the installed 2026.03.03 source:
  - One part per available cookie, joined by a single space:
    - `SAPISIDHASH` from `SAPISID` (falls back to `__Secure-3PAPISID`)
    - `SAPISID1PHASH` from `__Secure-1PAPISID`
    - `SAPISID3PHASH` from `__Secure-3PAPISID`
  - Each part: `"<SCHEME> " + ts + "_" + sha1hex(hash_input) [+ "_u"]`.
  - `hash_input` is `ts + " " + sid + " " + origin`. When a user session id is known, it becomes `user_session_id + " " + ts + " " + sid + " " + origin` and the part gets the suffix `_u`.
  - `user_session_id` comes from the page `ytcfg.USER_SESSION_ID`, or from `DATASYNC_ID` (format `DELEGATED_SESSION_ID||USER_SESSION_ID`).
  - Test vector for one `_u` part (same inputs as above, `user_session_id = 123456789012345678901`): `SAPISIDHASH 1700000000_d335549008304056022c188f74e2d0a8b2160411_u`.

### 4.4 Session lifetime: reported problems

- ytmusicapi docs (`docs/source/setup/browser.rst`) say browser credentials stay valid for roughly two years, until logout.
- **[issue #962, 2026-07-24, closed]**: copied cookies give a **guest session on a different public IP**. The rotating `__Secure-1PSIDTS` / `__Secure-3PSIDTS` cookies appear to be validated against the originating IP. With the same IP everything works, even though `account_menu` may report `logged_in: 0`. For us, WebView2 and the app run on the same machine and IP, which is fine.
- **[issue #1016, 2026-09-23]** and **[#921 comment, 2026-05-06]**: cookie sessions sometimes die after hours to about 2 days, with no sign-out event. The symptom: `browse FEmusic_history` returns a "Sign in to view your history" `itemSectionRenderer`/`messageRenderer` instead of data.
- **[issue #676, 2024-11-18]**: the maintainer recommends creating the cookies in a **private/incognito** browser session so the regular browser does not rotate them. For WebView2, the equivalent is a dedicated user-data folder used only for login.
- Consequence: the app must detect a dead session and offer a re-login. That means opening the sign-in WebView2 again. See section 9.

### 4.5 Brand accounts

**[ytm]**: pass the 21-digit brand id as `context.user.onBehalfOfUser`; the headers do not change (`docs/source/usage.rst`). The web app uses `X-Goog-PageId` instead **[issue #283]**, but ytmusicapi does not.

---

## 5. OAuth (device flow)

### 5.1 What ytmusicapi does

Constants **[ytm]** `constants.py`:

```
OAUTH_SCOPE      = "https://www.googleapis.com/auth/youtube"
OAUTH_CODE_URL   = "https://www.youtube.com/o/oauth2/device/code"      <- NOT oauth2.googleapis.com/device/code
OAUTH_TOKEN_URL  = "https://oauth2.googleapis.com/token"
OAUTH_USER_AGENT = USER_AGENT + " Cobalt/Version"
                 = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0 Cobalt/Version"
```

All three OAuth requests go through `auth/oauth/credentials.py:OAuthCredentials._send_request`:

- `POST`, form-encoded (`application/x-www-form-urlencoded`)
- Header `User-Agent: <OAUTH_USER_AGENT>`
- `client_id` is always added to the form.

**a) Device code**, `OAuthCredentials.get_code`:

```
POST https://www.youtube.com/o/oauth2/device/code
scope=https://www.googleapis.com/auth/youtube&client_id=<id>
```

There is **no client_secret** in this request. The response (`auth/oauth/models.py:AuthCodeDict`) contains:

- `device_code`
- `user_code` (format `XXX-XXX-XXX`)
- `expires_in`
- `interval` (about 5)
- `verification_url` (note: `_url`, not `_uri`)

The URL shown to the user is `f"{verification_url}?user_code={user_code}"` (`auth/oauth/token.py:RefreshingToken.prompt_for_token`). A 2026 log in #921 shows `https://www.google.com/device?user_code=...`.

**b) Token**, `OAuthCredentials.token_from_code`:

```
POST https://oauth2.googleapis.com/token
client_secret=<secret>&grant_type=http://oauth.net/grant_type/device/1.0&code=<device_code>&client_id=<id>
```

ytmusicapi uses the **legacy** grant type `http://oauth.net/grant_type/device/1.0` and the parameter name **`code`**. It does not use the RFC 8628 `urn:ietf:params:oauth:grant-type:device_code` with `device_code`.

The response contains `access_token`, `expires_in`, `refresh_token`, `scope`, `token_type` (`"Bearer"`), and, since about March 2026, **`refresh_token_expires_in`**. That last field crashed older versions; fixed in #932, 2026-06-05.

**c) Refresh**, `OAuthCredentials.refresh_token`:

```
POST https://oauth2.googleapis.com/token
client_secret=<secret>&grant_type=refresh_token&refresh_token=<rt>&client_id=<id>
```

The response contains `access_token` and `expires_in`; ytmusicapi keeps the old refresh token.

**Polling:** ytmusicapi **does not poll**. `prompt_for_token` blocks on `input()` until the user presses Enter, then calls the token endpoint **once**.

- Error handling covers only HTTP 401: `unauthorized_client` → `UnauthorizedOAuthClient`; `invalid_client` → `BadOAuthClient`, "client_id/secret mismatch or YouTube Data API not enabled".
- Polling at `interval` and handling `authorization_pending` / `slow_down` / `access_denied` / `expired_token` would be our addition, based on Google's device-flow documentation **[not-ytm, uncertain on exact status codes]**.

**Refresh logic** `auth/oauth/token.py:RefreshingToken.__getattribute__`:

- On every read of `access_token`, if `expires_at - now < 60` seconds, it refreshes.
- It sets `expires_at = now + expires_in` (`OAuthToken.update`) and rewrites the token file.

**Headers on InnerTube with OAuth** **[ytm]** `YTMusicBase.headers`:

- `initialize_headers()`
- `X-Goog-Visitor-Id`
- `authorization: Bearer <access_token>` (`Token.as_auth` = `f"{token_type} {access_token}"`)
- `X-Goog-Request-Time: <unix seconds>`

The query string is `?alt=json` (**no key**). There is **no** `X-Goog-AuthUser`, and the cookie is only `SOCS=CAI` (+ session jar).

**Does OAuth change the context?** No. It stays `WEB_REMIX` / date version, same as unauthenticated.

**Is a custom client id/secret mandatory?** Yes, since November 2024 (ytmusicapi 1.9.0):

- `docs/source/setup/oauth.rst` (attention box): YouTube Music requires a Client Id and Secret since November 2024, and the client type must be "TVs and Limited Input devices".
- `YTMusicBase.__init__` raises if an OAuth token is passed without `oauth_credentials`. There is no built-in default client any more (`constants.py` has none).
- Background: #676 (2024-11-09) and #679.
- The Google Cloud project needs the YouTube Data API enabled (`BadOAuthClient` message).

### 5.2 Current status: OAuth does **not** work against InnerTube

Timeline from the issue trackers:

| Date | Event |
|---|---|
| 2024-11-05 → 11-16 | First OAuth break, affecting every third-party client: ytmusicapi **#676** (closed 2024-11-18) and yt-dlp **#11462** (closed 2024-11-16). ytmusicapi worked around it in 1.9.0 by requiring the user's own TV-type client (#679). |
| **2025-08-29** | Second break. Every InnerTube call with a Bearer token returns `HTTP 400 "Request contains an invalid argument."`. Unauthenticated calls still work. |
| 2025-09-02 | **#813** opened by the maintainer, labels `bug`, `help wanted`, `yt-update`. The issue body recommends browser auth as the workaround. **Still OPEN on 2026-10-07.** |
| 2025-09-03 | #814 (duplicate): the same token works against the official YouTube Data API v3 but fails on `/youtubei/v1/browse`. |
| 2025-09-07 → 10-02 | PR **#817** (switch to `IOS_MUSIC`): the maintainer and a collaborator could not make it work (CI: 88 failed, 31 passed). A contributor reports that `TVHTML5` clientVersion ≥ 7 accepts OAuth but returns **TV-shaped responses**, not YT Music render trees. The maintainer considers that API too limited to be worth new parsers. |
| 2025-10-02 | Maintainer on #813: there is no solution; browser auth is required. |
| 2026-05-01 → 06-15 | #921 "OAuth authentication still failing" is closed as the same root cause as #813. |
| 2026-07-31 | Comment on #813: the same `oauth.json` token still works against the official **YouTube Data API v3** (`/youtube/v3/playlists`, `playlistItems`), but not against InnerTube. |
| 2026-08-29 | Fresh reproduction on 1.12.2: every authenticated call fails, including `search()`. |
| **2026-09-01** | Maintainer on #813: "It's not a fight that we can possibly win." He sees no path forward, and Google offers nothing beyond the limited Data API. |

Additional evidence inside the repo:

- `tests/conftest.py:fixture_yt_oauth` returns a **browser-auth** client, with a comment that it replaces OAuth "due to oauth not working, see #813".
- Stale docs: `docs/source/setup/index.rst` still presents OAuth as the easiest method. `setup.py:parse_args` labels `browser` as deprecated. Both statements contradict the code and the issues. Do not rely on them.

**Bottom line:** OAuth device flow + Bearer on InnerTube is **non-functional** today. Cookies + SAPISIDHASH is the only authenticated mode that works with the `WEB_REMIX` render trees our parsers target. A Bearer token is still useful only for the **official YouTube Data API v3**. That is a different, quota-limited API, it is not InnerTube, and it lacks home, radio, lyrics and similar features.

---

## 6. MVP endpoints

Every request below is `POST {base}/{endpoint}{params}` with the JSON body shown, plus `"context"` (section 2). "Auth" means:

- **required**: ytmusicapi calls `_check_auth()` and refuses unauthenticated use.
- **optional**: works anonymously; personalised when authenticated.

`validate_playlist_id(id)` (`parsers/playlists.py`) **strips a leading `VL`**. Every edit/next endpoint uses it.

### 6.1 `browse`

| Function (file) | Body | Auth | Pagination |
|---|---|---|---|
| `get_home(limit=3)` (`mixins/browsing.py`) | `{"browseId": "FEmusic_home"}` | optional | `sectionListContinuation`, query string |
| `get_library_playlists(limit=25)` (`mixins/library.py`) | `{"browseId": "FEmusic_liked_playlists"}` | required | `gridContinuation`, query string. The first grid item (the "New playlist" tile) is skipped. |
| `get_library_songs(limit=25, validate_responses=False, order=None)` | `{"browseId": "FEmusic_liked_videos"}` + optional `"params"` (order) | required | `musicShelfContinuation`, query string. 25 per page. With `validate_responses`, a short page is re-requested up to 3 times. The first item, a "shuffle all" entry, is dropped when there are ≥ 2 items (`parsers/library.py:pop_songs_random_mix`). |
| `get_library_albums(limit=25, order=None)` | `{"browseId": "FEmusic_liked_albums"}` + optional `"params"` | required | `gridContinuation`, query string |
| `get_library_artists(limit=25, order=None)` | `{"browseId": "FEmusic_library_corpus_track_artists"}` + optional `"params"` | required | `musicShelfContinuation`, query string |
| `get_library_subscriptions(limit=25, order=None)` | `{"browseId": "FEmusic_library_corpus_artists"}` + optional `"params"` | required | `musicShelfContinuation`, query string |
| `get_liked_songs(limit=100)` (`mixins/playlists.py`) | delegates to `get_playlist("LM")`, i.e. `{"browseId": "VLLM"}` | required | 2025 style (body) |
| `get_history()` (`mixins/library.py`) | `{"browseId": "FEmusic_history"}` | required | **none**. All sections ("Today", "Yesterday", ...) come in one response. |
| `get_playlist(playlistId, limit=100, related=False, suggestions_limit=0)` | `{"browseId": "VL" + playlistId}`. The `VL` is added **unless the id already starts with `VL`**. | optional (needed for private playlists) | tracks: **2025 style (body)**. Suggestions and related: query string. |
| `get_album(browseId)` (`mixins/browsing.py`) | `{"browseId": "MPREb_..."}`. It **must start with `MPRE`**, otherwise ytmusicapi raises. | optional | none |
| `get_artist(channelId)` | `{"browseId": channelId}`, with a leading `MPLA` stripped (`channelId.removeprefix("MPLA")`) | optional | none (see `get_artist_albums`) |
| `get_artist_albums(channelId, params, limit=100, order=None)` | `{"browseId": channelId, "params": params}`, both taken from `get_artist` | optional | `gridContinuation`, query string. Sort order uses `reloadContinuationData` taken from the sort menu. |
| `get_song_related(browseId)` | `{"browseId": "MPTRt..."}` (from `next`) | optional | none |
| `get_lyrics(browseId)` | see 6.5 | optional | none |

Library order params **[ytm]** `mixins/_utils.py:prepare_order_params`:

| order | params value |
|---|---|
| `a_to_z` | `ggMGKgQIARAA` |
| `z_to_a` | `ggMGKgQIARAB` |
| `recently_added` | `ggMGKgQIABAB` |

**Album ids.** `get_album` needs the **browseId** (`MPREb_...`). Its response contains `audioPlaylistId` (`OLAK5uy_...`), which comes from the play button's `watchPlaylistEndpoint.playlistId` or `watchEndpoint.playlistId` (`parsers/albums.py`).

- Use the `OLAK5uy_` id to **play** the album: `next` with `playlistId`, or radio `RDAMPL` + id.
- To go the other way (`OLAK5uy_` → `MPREb_`), use `get_album_browse_id(audioPlaylistId)`. It makes a plain `GET https://music.youtube.com/playlist?list=<OLAK5uy_...>` with the normal headers, decodes `\uXXXX` escapes, and regex-matches the first `"MPRE.+?"`. It does **not** use InnerTube.
- `get_playlist("OLAK5uy_...")` also works. If the response has no playlist header, ytmusicapi switches to `parsers/playlists.py:parse_audio_playlist`.

**Artist page.** The response is usually `singleColumnBrowseResultsRenderer`, but some artist pages return `twoColumnBrowseResultsRenderer` (#929); handle both. Each shelf (songs/albums/singles/videos/...) may carry:

- a `browseId`. For songs or videos this is a playlist id: pass it to `get_playlist`.
- a `browseId` + `params` for albums/singles: pass them to `get_artist_albums`.

Radio and shuffle ids come from the header buttons: `radioId` from `startRadioButton` (`watchEndpoint` or `watchPlaylistEndpoint` `.playlistId`), `shuffleId` from `playButton`.

### 6.2 `search`

`search(query, filter=None, scope=None, limit=20, ignore_spelling=False)` (`mixins/search.py`). Body: `{"query": "<text>"}` plus `"params": <string>` when the params are not `None`. Params come from `parsers/search.py:get_search_params`.

The values contain a literal `%3D`. **Send them exactly as below, including `%3D`, inside the JSON string.** Do not URL-decode them.

| scope | filter | ignore_spelling = False | ignore_spelling = True |
|---|---|---|---|
| — | *(none)* | *(no params)* | `EhGKAQ4IARABGAEgASgAOAFAAUICCAE%3D` |
| — | songs | `EgWKAQIIAWoMEA4QChADEAQQCRAF` | `EgWKAQIIAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | videos | `EgWKAQIQAWoMEA4QChADEAQQCRAF` | `EgWKAQIQAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | albums | `EgWKAQIYAWoMEA4QChADEAQQCRAF` | `EgWKAQIYAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | artists | `EgWKAQIgAWoMEA4QChADEAQQCRAF` | `EgWKAQIgAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | playlists (all) | `Eg-KAQwIABAAGAAgACgBMABqChAEEAMQCRAFEAo%3D` | `Eg-KAQwIABAAGAAgACgBMABCAggBagoQBBADEAkQBRAK` |
| — | community_playlists | `EgeKAQQoAEABagwQDhAKEAMQBBAJEAU%3D` | `EgeKAQQoAEABQgIIAWoMEA4QChADEAQQCRAF` |
| — | featured_playlists | `EgeKAQQoADgBagwQDhAKEAMQBBAJEAU%3D` | `EgeKAQQoADgBQgIIAWoMEA4QChADEAQQCRAF` |
| — | profiles | `EgWKAQJYAWoMEA4QChADEAQQCRAF` | `EgWKAQJYAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | podcasts | `EgWKAQJQAWoMEA4QChADEAQQCRAF` | `EgWKAQJQAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| — | episodes | `EgWKAQJIAWoMEA4QChADEAQQCRAF` | `EgWKAQJIAUICCAFqDBAOEAoQAxAEEAkQBQ%3D%3D` |
| library | *(none)* | `agIYBA%3D%3D` | same |
| library | songs | `EgWKAQIIAWoKEAUQCRADEAoYBA%3D%3D` | same (spelling flag ignored) |
| library | videos | `EgWKAQIQAWoKEAUQCRADEAoYBA%3D%3D` | same |
| library | albums | `EgWKAQIYAWoKEAUQCRADEAoYBA%3D%3D` | same |
| library | artists | `EgWKAQIgAWoKEAUQCRADEAoYBA%3D%3D` | same |
| library | playlists | `EgWKAQIoAWoKEAUQCRADEAoYBA%3D%3D` | same |
| library | profiles / podcasts / episodes | `EgWKAQJYAW...` / `EgWKAQJQAW...` / `EgWKAQJIAW...`, each followed by `oKEAUQCRADEAoYBA%3D%3D` | same |
| uploads | *(none only; a filter is rejected)* | `agIYAw%3D%3D` | same |

Rules enforced by ytmusicapi:

- `community_playlists` and `featured_playlists` are rejected with `scope="library"`.
- Any filter is rejected with `scope="uploads"`.

Response tab selection: if `contents.tabbedSearchResultsRenderer` exists, ytmusicapi uses `tabs[0]`. The exception is a scope **without** a filter: then it uses tab `1` for library and tab `2` for uploads.

- **Continuations** happen only when a filter is set: `musicShelfContinuation`, query-string style, **same body** (query + params). **[live]**: page 1 of `songs` returned 20 items with `nextContinuationData`, and page 2 returned 20 more.
- Auth: optional. When authenticated, searches are added to the account's search history (maintainer, #703, 2024-12-28).

### 6.3 `music/get_search_suggestions`

`get_search_suggestions(query, detailed_runs=False)`. Body: `{"input": "<partial text>"}`. Auth is optional; with auth the response includes history suggestions (`historySuggestionRenderer` carrying a `feedbackToken`).

Removing a history suggestion: `remove_search_suggestions` → endpoint `feedback`, body `{"feedbackTokens": ["..."]}`.

### 6.4 `next` (watch queue, radio, shuffle, lyrics and related ids)

`get_watch_playlist(videoId=None, playlistId=None, limit=25, radio=False, shuffle=False)` (`mixins/watch.py`). The body is built in this order:

```jsonc
{
  "enablePersistentPlaylistPanel": true,
  "isAudioOnly": true,
  "tunerSettingValue": "AUTOMIX_SETTING_NORMAL",
  "videoId": "<videoId>",                       // only if videoId given
  "watchEndpointMusicSupportedConfigs": {        // only if videoId given AND not radio AND not shuffle
    "watchEndpointMusicConfig": {
      "hasPersistentPlaylistPanel": true,
      "musicVideoType": "MUSIC_VIDEO_TYPE_ATV"
    }
  },
  "playlistId": "<id with leading VL stripped>", // see defaulting rule below
  "params": "wAEB"                               // radio=true      (wins over shuffle)
  // "params": "wAEB8gECKAE%3D"                  // shuffle=true and playlistId given
}
```

At least one of `videoId` and `playlistId` is required.

- If only `videoId` is given, ytmusicapi sets **`playlistId = "RDAMVM" + videoId`**. That is the song radio, and it is what the web app does when you press play on a single song.
- `radio=True` → `params: "wAEB"`. Typical radio calls: `videoId` + `radio=True`, or `playlistId = "RDAMPL" + <playlist/album id>` (FAQ: songs/videos `RDAMVM`+videoId; playlists/albums `RDAMPL`+playlistId).
- `shuffle=True` with a playlistId → `params: "wAEB8gECKAE%3D"`. Ignored if `radio=True`. The FAQ mentions `get_watch_playlist_shuffle`, but that function **no longer exists** in the source.
- Normal queue for an album or playlist: `videoId` (optional) + `playlistId` (`OLAK5uy_...`, `PL...`, `VL...` stripped).

Response handling **[ytm]**:

- Root: `contents.singleColumnMusicWatchNextResultsRenderer.tabbedRenderer.watchNextTabbedResultsRenderer`.
- Queue: `tabs[0].tabRenderer.content.musicQueueRenderer.content.playlistPanelRenderer.contents`. An empty value means the server returned nothing; a private playlist can cause this.
- Returned `playlistId`: the first `playlistPanelVideoRenderer.navigationEndpoint.watchEndpoint.playlistId` found.
- **Lyrics and related browseIds** (`parsers/watch.py:get_tab_browse_ids`): loop over `tabs[]`, **skip tabs that have an `unselectable` key** (this means no lyrics), and read `tabRenderer.endpoint.browseEndpoint.browseId` keyed by `browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType`:
  - `MUSIC_PAGE_TYPE_TRACK_LYRICS` → lyrics browseId (`MPLYt...`)
  - `MUSIC_PAGE_TYPE_TRACK_RELATED` → related browseId (`MPTRt...`)
- **[live]**: tabs are currently `Up next`, `Lyrics` (MPLYt..., TRACK_LYRICS), `Comments` (no browse id) and `Related` (MPTRt..., TRACK_RELATED). The radio queue returned 50 items plus `nextRadioContinuationData`.
- Continuation: `playlistPanelContinuation`, query-string style, **same body**. The token path is `continuations[0].nextRadioContinuationData.continuation` for radio-like ids. It is `nextContinuationData` when the playlist id starts with `PL` or `OLA` (`ctoken_path = "" if is_playlist else "Radio"`).
- Caveat in the docstring: a track `likeStatus` of `INDIFFERENT` from this endpoint may really be `DISLIKE`.

### 6.5 Lyrics (`browse`)

`get_lyrics(browseId, timestamps=False)` (`mixins/browsing.py`). `browseId` is the `MPLYt...` id from `next`.

- **Plain:** `browse` with `{"browseId": "MPLYt..."}`, normal context. The text is at `contents.sectionListRenderer.contents[0].musicDescriptionShelfRenderer.description.runs[0].text`.
- **Timestamped:** the **same request** with the context temporarily switched to `clientName "ANDROID_MUSIC"`, `clientVersion "7.21.50"` (`as_mobile()`). Headers are unchanged.
  - Data is at `contents.elementRenderer.newElement.type.componentType.model.timedLyricsModel.lyricsData`. It contains `timedLyricsData[]`, where each item has `lyricLine` and `cueRange{startTimeMilliseconds, endTimeMilliseconds, metadata.id}`, plus `sourceMessage`.
  - If **any** line lacks `cueRange`, ytmusicapi falls back to plain text built by joining the `lyricLine` values (fix for #1002, closed 2026-09-15).
  - If the mobile response has no timed lyrics, it falls back to the plain path.
- **What the app does (2026-10-07):** the timed request is sent **without the account** (no cookies, no SAPISIDHASH). ytmusicapi only tests it unauthenticated, #828 reports HTTP 400 for it when signed in, and an Android-client reply must never mark the cookie session as expired.
  - If the mobile reply has no lyrics, or fails with an HTTP error, the app repeats the normal (web, signed-in) request.
  - A song with unsynced lyrics returns lines without `cueRange` in mobile mode, so it shows as plain text.

### 6.5a Related (`browse`, `get_song_related`)

`browse` with `{"browseId": "MPTRt..."}`, the related id from `next`. The response is `contents.sectionListRenderer.contents[]` of carousel shelves ("You might also like", "Recommended playlists", "Similar artists", albums), parsed like home (`parse_mixed_content`). "About the artist" is a description shelf with no items; the app keeps its text as the shelf subtitle.

### 6.5b Loudness (`player`), for volume normalization

The same `player` request as `get_song` (see 6.10 for the body). The app reads `playerConfig.audioConfig.loudnessDb`: the track's loudness relative to YouTube's target (`loudnessTargetLkfs`, -7 in the samples). Positive means louder than the target. If it is missing, the app uses `perceptualLoudnessDb - loudnessTargetLkfs`. Like the web player, it only ever turns tracks down: gain = 10^(-dB/20) when dB > 0.
- It works **anonymously**: the app sends it without the account, because it runs for every played track.
- Age-gated (`LOGIN_REQUIRED`) and unavailable tracks have no `playerConfig`, so they get no gain.
- Results are cached per video in memory.

### 6.6 Like / dislike / remove like

`rate_song(videoId, rating)` (`mixins/library.py`). Body `{"target": {"videoId": "<id>"}}`. Auth required. The endpoint comes from `mixins/_utils.py:prepare_like_endpoint`:

| rating | endpoint |
|---|---|
| `LIKE` | `like/like` |
| `DISLIKE` | `like/dislike` (yes, it exists) |
| `INDIFFERENT` | `like/removelike` |

`rate_playlist(playlistId, rating)` uses the same endpoints with body `{"target": {"playlistId": "<id>"}}`. This saves or removes an album/playlist from the library.

Liking a song does **not** add it to the library (FAQ). The library add/remove is `edit_song_library_status`: endpoint `feedback`, body `{"feedbackTokens": [...]}`, using the add/remove tokens from track menus. Remove items from Liked Music with `rate_song(..., INDIFFERENT)`, not with `remove_playlist_items` (#453).

### 6.7 Playlists: create / edit / delete

All require auth. Playlist ids are passed through `validate_playlist_id`, which strips `VL`.

**`playlist/create`**, `create_playlist(title, description, privacy_status="PRIVATE", video_ids=None, source_playlist=None)`:

```json
{ "title": "My list",
  "description": "text, HTML tags removed",
  "privacyStatus": "PRIVATE",
  "videoIds": ["..."],
  "sourcePlaylistId": "PL..." }
```

- `privacyStatus` is one of `PRIVATE` | `PUBLIC` | `UNLISTED`.
- `videoIds` and `sourcePlaylistId` are optional.
- The title must not contain `<` or `>` (ytmusicapi rejects it, because YTM breaks). The description is passed through `html_to_txt`.
- Success: the response has `"playlistId"`.
- **Gated response:** HTTP 200 where `actions[0].showEngagementPanelEndpoint` exists. YTM did not perform the action and wants a dialog. ytmusicapi raises `YTMusicGatedError` (`mixins/_utils.py:validate_write_response`).
- Tests note: the first edit of a freshly created playlist can fail with **409 Conflict** (or 400) for up to about 20 s (`tests/mixins/test_playlists.py:retry_playlist_edit`, 8 attempts, 5 s apart).

**`browse/edit_playlist`**. The body is always `{"playlistId": "<id>", "actions": [ ... ]}`.

| Function | Action object(s) |
|---|---|
| `add_playlist_items(playlistId, videoIds, source_playlist=None, duplicates=False)` | One per video: `{"action": "ACTION_ADD_VIDEO", "addedVideoId": "<videoId>"}`. If `duplicates=True`, each also gets `"dedupeOption": "DEDUPE_OPTION_SKIP"`. With `source_playlist`: `{"action": "ACTION_ADD_PLAYLIST", "addedFullListId": "<id>"}`, and if no videoIds were given, also `{"action": "ACTION_ADD_VIDEO", "addedVideoId": null}` (otherwise YTM does not return the setVideoId map). |
| `remove_playlist_items(playlistId, videos)` | One per item: `{"setVideoId": "<setVideoId>", "removedVideoId": "<videoId>", "action": "ACTION_REMOVE_VIDEO"}`. Items lacking either id are filtered out; if none remain, ytmusicapi raises "Do you own this playlist?". |
| `edit_playlist(title=...)` | `{"action": "ACTION_SET_PLAYLIST_NAME", "playlistName": "..."}` |
| `edit_playlist(description=...)` | `{"action": "ACTION_SET_PLAYLIST_DESCRIPTION", "playlistDescription": "..."}` |
| `edit_playlist(privacyStatus=...)` | `{"action": "ACTION_SET_PLAYLIST_PRIVACY", "playlistPrivacy": "PUBLIC"}` |
| `edit_playlist(moveItem=setVideoId or (setVideoId, successorSetVideoId))` | `{"action": "ACTION_MOVE_VIDEO_BEFORE", "setVideoId": "<item to move>", "movedSetVideoIdSuccessor": "<item it should end up before>"}`. The successor is optional. |
| `edit_playlist(addPlaylistId=...)` | `{"action": "ACTION_ADD_PLAYLIST", "addedFullListId": "..."}` |
| `edit_playlist(sortOrder=...)` | `{"action": "ACTION_SET_PLAYLIST_VIDEO_ORDER", "playlistVideoOrder": 0/1/2/6}` (MANUAL / NEWEST_FIRST / NEWEST_LAST / TOP_VOTED) |
| `edit_playlist(addToTop=bool)` | `{"action": "ACTION_SET_ADD_TO_TOP", "addToTop": "true"/"false"}` (a **string**) |
| `edit_playlist(collaboration=True/False)` | `{"action": "ACTION_CREATE_COLLABORATION_INVITE_LINK"}` / `{"action": "ACTION_SET_CLOSED_TO_CONTRIBUTIONS", "closedToContributions": true}` |
| `edit_playlist(voteOption=...)` | `{"action": "ACTION_SET_ALLOW_ITEM_VOTE", "itemVotePermission": 1/2/3}` |

Several actions can be sent in one request; `edit_playlist` batches them.

**Duplicate handling.** The parameter name is counter-intuitive:

- `duplicates=True` → `dedupeOption: DEDUPE_OPTION_SKIP`, meaning the server's duplicate check is skipped and duplicates **are** added. The tests add the same video 101 times and expect 101 tracks.
- `duplicates=False` (no dedupeOption) → per the docstring, if any video is already in the playlist, an error is returned and **nothing** is added. The exact response shape of that error is **[uncertain]**; it is not in the source. Capture a fixture.

**Success response:** `"status": "STATUS_SUCCEEDED"` (`enums.py:ResponseStatus`). For adds, `playlistEditResults[].playlistEditVideoAddedResultData` maps `videoId` → new `setVideoId`. `setVideoId` is the unique id of a playlist **entry**; it is needed to remove or move that entry. Get it from `get_playlist` tracks.

**`playlist/delete`**: `delete_playlist(playlistId)` with body `{"playlistId": "<id>"}`. It returns `status`.

### 6.8 Account info, for "Signed in as" (not in the MVP list, but needed)

`get_account_info()` (`mixins/library.py`): endpoint **`account/account_menu`**, body `{}`, auth required. Data is at `actions[0].openPopupAction.popup.multiPageMenuRenderer.header.activeAccountHeaderRenderer`, which has `accountName`, `channelHandle` and `accountPhoto`. #962 notes that `responseContext.serviceTrackingParams` reports `logged_in`.

### 6.9 Other endpoints we will touch

- `feedback`: library add/remove and history removal (`remove_history_items`, body `{"feedbackTokens": [...]}`).
- `subscription/subscribe`: body `{"channelIds": ["<id>"]}`. `subscription/unsubscribe` takes the same body.

### 6.9a Explore, moods & genres, charts, artist "See all"

All `browse` requests, as in ytmusicapi:

- `get_explore()`: `{"browseId": "FEmusic_explore"}`. New releases, trending (or "Top songs" for Premium), the mood tiles, top music videos and new videos.
- `get_mood_categories()`: `{"browseId": "FEmusic_moods_and_genres"}`. Tile titles, their `params` and tint colours.
- `get_mood_playlists(params)`: `{"browseId": "FEmusic_moods_and_genres_category", "params": <params>}`.
- `get_charts(country)`: `{"browseId": "FEmusic_charts", "formData": {"selectedValues": [<country>]}}`. The country comes from Settings → Content country, else "ZZ" (global).
- `get_artist_albums(channelId, params)`: browse with the shelf's "More" `browseId` and `params`, plus its continuation.

Where the app goes further than ytmusicapi:

- The two full new-release lists have no ytmusicapi call. The app follows the Explore page's own "More" links (`FEmusic_new_releases_albums`, `FEmusic_new_releases_videos`).
- ytmusicapi's `get_mood_playlists` fails on genre pages that mix shelf types. The app keeps each section as a shelf and parses every card by its own type.
- It also reads the trending rank and the tile colours.

### 6.10 `add_history_item`: the full flow

**[ytm]** `mixins/library.py:add_history_item(song)` + `mixins/browsing.py:get_song(videoId, signatureTimestamp=None)`.

1. **`player` endpoint** (`get_song`), using the **same authenticated credentials** you will use in step 2:

   ```json
   { "playbackContext": { "contentPlaybackContext": { "signatureTimestamp": 20732 } },
     "video_id": "<videoId>",
     "context": { ... } }
   ```

   - The field is named **`video_id`** (snake_case) in ytmusicapi.
   - `signatureTimestamp` defaults to `get_datestamp() - 1`, i.e. the number of whole days since 1970-01-01 UTC, minus 1 (`mixins/_utils.py:get_datestamp`). The exact value can be scraped from `base.js` (`get_signatureTimestamp`), but that matters only for stream URLs, which we do not take from here.
   - ytmusicapi keeps only `videoDetails`, `playabilityStatus`, `streamingData`, `microformat` and `playbackTracking` from the response.
2. Read `playbackTracking.videostatsPlaybackUrl.baseUrl`. This is an `https://s.youtube.com/api/stats/playback?...` URL with an existing query string.
3. Generate a **cpn** (client playback nonce): **16 characters**, each `CPNA[randint(0, 256) & 63]`, with `CPNA = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_"`. In effect each character is chosen uniformly from the 64 URL-safe characters.
4. `GET baseUrl` with extra query parameters `ver=2`, `c=WEB_REMIX`, `cpn=<cpn>`, appended to the existing query string. Use `self.headers`: the **same auth headers** (cookie + SAPISIDHASH, or Bearer) and the `SOCS` cookie.
5. Success is HTTP **204**.

Caveats:

- **[issue #703]**: the GET returns 204 **even when nothing is recorded**, e.g. if step 1 was done anonymously. History is only updated if `get_song` was called with the authenticated session. The only way to confirm is to re-read `get_history()` a few seconds later.
- ytmusicapi only pings `videostatsPlaybackUrl`. The response also has `videostatsWatchtimeUrl`, `ptrackingUrl`, `qoeUrl` and `atrUrl`, which the web app pings during playback. ytmusicapi does not, and that works for history.
- The ping goes to **`s.youtube.com`**, not `music.youtube.com`. Our HTTP layer must allow it and attach the same cookies/authorization. Cookie mode: the SAPISIDHASH origin stays `https://music.youtube.com`, exactly like ytmusicapi, which reuses `self.headers`.

---

## 7. Continuations (pagination)

ytmusicapi has two mechanisms (`continuations.py`).

### A. Legacy query-string style

Used by `get_continuations`, `get_validated_continuations` and `get_reloadable_continuations`.

- Token: `<renderer>.continuations[0].next{ctoken_path}ContinuationData.continuation`. `ctoken_path` is `""` or `"Radio"`. For "reloadable" continuations the path is `continuations[0].reloadContinuationData.continuation`.
- Request: **the same endpoint and the same body** as the first page, with `"&ctoken=" + token + "&continuation=" + token` appended to the URL (`get_continuation_string`). The token is appended **raw**: it contains only `[A-Za-z0-9%]`, already percent-encoded **[live]**. Do not encode it again.
- Response: `continuationContents.<continuation_type>`, with items in `contents` or `items`. The loop stops when `continuationContents` is missing, when a page is empty, or when `limit` is reached.

### B. "2025" body style

Used by `get_continuations_2025` (playlists, since early 2025, #734).

- Token: in the **last item** of the item list, `continuationItemRenderer.continuationEndpoint.continuationCommand.token`. Alternatively it can be in `continuationItemRenderer.continuationEndpoint.commandExecutorCommand.commands[]`, at the `continuationCommand` whose `request == "CONTINUATION_REQUEST_TYPE_BROWSE"`.
- Request: `POST browse` with body **`{"continuation": "<token>"}` + context only**. There is **no** `browseId` and **no** query-string token.
- Response: `onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems`. The next token is again in the last item.
- **[live]**: a public playlist returned 101 items and a body-style token, with no legacy `continuations` key. The continuation returned 101 more.

### Per-endpoint style (today)

| ytmusicapi function | Endpoint | Style | `continuation_type` / notes |
|---|---|---|---|
| `get_home` | browse | A | `sectionListContinuation` |
| `get_library_playlists` | browse | A | `gridContinuation` |
| `get_library_songs` | browse | A | `musicShelfContinuation` |
| `get_library_albums` | browse | A | `gridContinuation` |
| `get_library_artists` / `get_library_subscriptions` | browse | A | `musicShelfContinuation` |
| `get_history` | browse | — | not paginated |
| `get_playlist` / `get_liked_songs`: **tracks** | browse | **B** | (also OLAK audio playlists) |
| `get_playlist`: suggestions | browse | A (`sectionListContinuation`, then reloadable `musicShelfContinuation`) | owned playlists only |
| `get_playlist`: related | browse | A (`sectionListContinuation`) | |
| `get_artist_albums` | browse | A | `gridContinuation`; sort via reloadable token |
| `search` (with a filter) | search | A | `musicShelfContinuation` **[live]** |
| `search` (no filter) | search | — | none |
| `get_watch_playlist` | next | A | `playlistPanelContinuation`; `nextRadioContinuationData` for radio ids, `nextContinuationData` for `PL`/`OLA` playlists |

---

## 8. yt-dlp reality check (2026-10-07)

### 8.1 Environment

- yt-dlp **2026.03.03**, installed with **pip**, not the standalone release exe. `pip list` shows **no `yt-dlp-ejs`** package.
- Node.js **v25** on PATH; no Deno, no Bun. ffmpeg present.
- Latest yt-dlp stable: **`2026.08.19`**, published 2026-08-19 (`gh release view`). Earlier tags: 2026.07.04, 2026.06.09, 2026.03.17, 2026.03.13. **The tested version was 7 months old.**

### 8.2 Results (metadata only, `dQw4w9WgXcQ`; same results for `kJQP7kiw5Fk`, `9bZkp7q19f0` and the `www.youtube.com` URL)

**1. The plain command:** `yt-dlp -f bestaudio -j --no-playlist "https://music.youtube.com/watch?v=dQw4w9WgXcQ"` → **exit 1**. Output on stderr:

```
WARNING: Your yt-dlp version (2026.03.03) is older than 90 days! ... You installed yt-dlp with pip ... Use that to update.
WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default; to use another
         runtime add  --js-runtimes RUNTIME[:PATH]  ... YouTube extraction without a JS runtime has been deprecated,
         and some formats may be missing. See  https://github.com/yt-dlp/yt-dlp/wiki/EJS
WARNING: [youtube] dQw4w9WgXcQ: Some android_vr client https formats have been skipped as they are missing a URL.
         YouTube may have enabled the SABR-only streaming experiment for the current session. (yt-dlp#12482)
ERROR: [youtube] dQw4w9WgXcQ: Requested format is not available.
```

**2. Same command + `--js-runtimes node`:** the JS-runtime warning disappears (`[jsc] JS Challenge Providers: ... node`), but **the same ERROR remains**. Verbose output shows:

- `web player response playability status: UNPLAYABLE`, and the same for `web_safari`
- `Detected experiment to bind GVS PO Token to video ID for web client`
- `PO Token Providers: none`
- `Forcing player 9f4cc5e4 ... "tv" player JS variant`

**3. Single clients (`--extractor-args youtube:player_client=X`):**

| Client | Result |
|---|---|
| `web` | "Video unavailable" |
| `web_music` | "The page needs to be reloaded." |
| `tv` | "The page needs to be reloaded." |

These failures are typical of outdated client versions in an old yt-dlp.

**4. `-F` (list formats):** the **only** media format is **18**:

| format_id | ext | Video | Audio | Bitrate | Source |
|---|---|---|---|---|---|
| 18 | mp4 | 640x360, `avc1.42001E` | `mp4a.40.2` (AAC-LC), 44.1 kHz (one video showed 22 kHz), 2 ch | ~444 kbps total | `c=ANDROID_VR` |

There are **no audio-only formats**: no 140 (m4a/AAC) and no 251 (webm/opus).

**5. `-f "bestaudio[ext=m4a]"`** → same ERROR. **`-f "bestaudio/best"`** → exit 0, picks format 18.

**6. Is the URL usable?** A `HEAD` request to the returned URL (no media bytes fetched) returned **`403 Forbidden`** (`Server: gvs 1.0`). This matches yt-dlp PR **#17461** (merged 2026-08-18, shipped in 2026.08.19): since 2026-08-17, **every** format from the `android_vr` client is 403'd, including itag 18. That PR removed `android_vr` from yt-dlp's default clients.

**7.** No "Sign in to confirm you're not a bot" message and no explicit n-challenge failure were seen. Extraction never reached those stages.

**Verdict:** with 2026.03.03, anonymous resolution **fails** (bestaudio) or **returns an unusable URL** (format 18 → 403). The app therefore requires yt-dlp 2026.08.19 or newer with a JS runtime, which resolves audio-only formats again.

### 8.3 How current yt-dlp wants to be run (README @ 2026.08.19)

- **EJS / JS runtime.** Full YouTube support needs **`yt-dlp-ejs`** plus a JS runtime: **deno** (recommended), **node**, **quickjs** or **bun**.
  - The **official executables bundle yt-dlp-ejs**. The README says `--remote-components` is unnecessary with an official executable.
  - Only **deno** is enabled by default. To use node: `--js-runtimes node`, or `--js-runtimes node:C:\path\to\node.exe`. To prefer node while deno is installed: `--no-js-runtimes --js-runtimes node`.
  - A pip install without `yt-dlp-ejs` (like the one tested above) would need `--remote-components ejs:github` or `ejs:npm`. That fetches JS at runtime; not tested.
- **Default player clients (2026.08.19):** `visionos,web`. Without a JS runtime, `web` is omitted. With logged-in cookies:
  - free accounts: `web_embedded,tv_downgraded,web`
  - premium accounts: `web_creator,tv_downgraded,web`
  - **`web_music` is added for `music.youtube.com` URLs** when logged-in cookies are used. `web_music` and `web_creator` **require a PO token** for their formats to be downloadable. yt-dlp ships no PO-token provider; that needs a plugin. Passing a `www.youtube.com/watch?v=` URL instead of the music URL avoids adding `web_music` **[inference, uncertain]**.
- **OAuth in yt-dlp: removed.** The installed source `yt_dlp/extractor/youtube/_base.py:_perform_login` raises "Login with OAuth is no longer supported" for any `--username oauth*`, and password login is not supported either. Cookies are the only auth: `--cookies <netscape file>` or `--cookies-from-browser`. History: yt-dlp **#11462** (OAuth 400, closed 2024-11-16).
- **Updating:**
  - `yt-dlp -U` updates **release binaries in place** (`yt-dlp.exe`) to the latest release of the current channel (`stable` by default).
  - `--update-to nightly|master|stable`, `--update-to stable@2026.08.19` or `--update-to 2026.08.19` switches channel, or upgrades/downgrades to a tag.
  - yt-dlp recommends **nightly** for regular users.
  - pip installs refuse to self-update (the warning above).
  - `--no-update` only suppresses the "older than 90 days" warning; it is the default behaviour otherwise.
  - The self-update replaces the exe file next to itself, so the exe must live in a **writable** folder. An MSIX install folder (`WindowsApps`) is read-only (section 9).

### 8.4 JSON fields to read from `-j`

Top level (the selected format's values are merged into the top level):

| Field | Use |
|---|---|
| `url` | stream URL. **Do not log it in full.** It contains `ip=` (IP-bound) and signatures. |
| `http_headers` | headers yt-dlp expects to be sent when fetching `url`. Observed: `User-Agent: ... Chrome/142 ...`, `Accept`, `Accept-Language: en-us,en;q=0.5`, `Sec-Fetch-Mode: navigate`. |
| `format_id`, `ext`, `acodec`, `vcodec` | `vcodec == "none"` means audio-only |
| `abr`, `asr`, `audio_channels`, `tbr` | bitrate info |
| `duration` (seconds, int), `duration_string` | duration |
| `filesize` / `filesize_approx` | size; often only the approx is present |
| `expire`, **inside `url`** | parse the `expire=<unix seconds>` query parameter. Observed: **6.0 h** after resolution. Re-resolve before expiry or on 403. |
| `available_at` | unix time before which the URL should not be fetched (forced pre-roll wait, yt-dlp #17062) **[uncertain semantics]**. Observed equal to the extraction time. |
| `downloader_options.http_chunk_size` | yt-dlp downloads YouTube in 10 MiB range chunks (10485760) to avoid throttling. Relevant if we proxy or stream ourselves. |
| `id`, `title`, `channel`, `thumbnail`, `is_live`, `availability` | metadata (prefer InnerTube for metadata) |
| `formats[]` | each entry has its own `url`, `http_headers`, `acodec`, `abr`, `ext`, `available_at` |

Example of a truncated URL: `https://rr1---sn-gqn-5gfe.googlevideo.com/videoplayback?expire=1791383...`. Query keys seen: `expire, ei, ip, id, itag, source, requiressl, ..., c, mime, dur, lmt, sig, lsig, ...`.

---

## 9. Original design assumptions vs. actual behaviour

Kept for history: what the client was first designed around, and what testing showed. The current decisions are listed in [CONTRIBUTING.md](../CONTRIBUTING.md#design-decisions).

| # | Assumption | Actual behaviour | Impact / outcome |
|---|---|---|---|
| a | URL `https://music.youtube.com/youtubei/v1/{endpoint}?prettyPrint=false` | ytmusicapi uses `?alt=json`, plus `&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30` with cookie auth (`constants.py`, `YTMusicBase.__init__`). **[live]**: both forms work; `prettyPrint=false` gives 60-70% smaller responses. | Low. Use `?alt=json&prettyPrint=false` (+ key in cookie mode). Keep the key configurable. |
| b | OAuth device flow (scope `youtube`, own TV client) + `Authorization: Bearer` works for InnerTube | **Broken since 2025-08-29**: HTTP 400 "Request contains an invalid argument." on every authenticated call. ytmusicapi #813 is OPEN; the maintainer sees no fix (2026-09-01); ytmusicapi's own tests use cookies instead. Also, ytmusicapi's device-code URL is `https://www.youtube.com/o/oauth2/device/code` (not `oauth2.googleapis.com/device/code`), it uses the legacy grant type `http://oauth.net/grant_type/device/1.0` with `code=`, and it sends the `X-Goog-Request-Time` header. | **High.** Outcome: cookie login is the only auth mode and there is no OAuth code. This also removes the need for a Google Cloud client id/secret. |
| c | "With OAuth only, stream resolution is anonymous" | True: yt-dlp removed OAuth ("Login with OAuth is no longer supported") and accepts cookies only. But it is moot because OAuth does not work for InnerTube either. Separately, **anonymous resolution itself is currently broken with the installed yt-dlp** (8.2), and for current yt-dlp it depends on the JS runtime and default clients (`visionos,web`). | Medium. Plan to pass the cookie-login cookies to yt-dlp (`--cookies`, Netscape format, domain `.youtube.com`). Note that cookies switch yt-dlp to other clients, and to `web_music` + PO token for music URLs (8.3). Test both anonymous and cookie paths with an up-to-date yt-dlp. |
| d | `yt-dlp.exe -f bestaudio -j --no-playlist <url>` is enough | With yt-dlp 2026.03.03: exit 1 ("Requested format is not available"); the only format offered is a 403 URL. Current yt-dlp needs a JS runtime: deno by default, otherwise `--js-runtimes node` (node is installed, deno is not). The official exe bundles yt-dlp-ejs; a pip install does not. The 90-day warning and other warnings go to **stderr**, and only stdout is JSON. | High. Use an up-to-date official `yt-dlp.exe` (stable ≥ 2026.08.19, or nightly as yt-dlp recommends). Suggested command: `-f "bestaudio[ext=m4a]/bestaudio/best" -j --no-playlist --js-runtimes node` (or deno). Parse stdout only; log stderr. Run the startup update check against a **writable copy** of the exe (see the MSIX row). |
| e | `Windows.Media.Playback.MediaPlayer` can play the chosen format | **M4A/AAC (itag 140, `mp4a.40.2`, fragmented MP4):** Media Foundation's MPEG-4 source and AAC decoder are in the box. **High confidence**, not tested here. **WebM/Opus (itag 251):** Windows 10/11 has the inbox "MKV Byte Stream Handler" (`mfmkvsrcsnk.dll`) registered for `.webm` / `audio/webm`, and a "Microsoft Opus Audio Decoder MFT", so it is **probably** playable. **Medium confidence**; seeking over HTTP is less certain. **Format 18 (MP4 H.264+AAC):** playable but wastes bandwidth on video, and currently 403. `MediaSource.CreateFromUri` cannot attach yt-dlp's `http_headers`; googlevideo usually does not need them **[uncertain]**. | Outcome (tested 2026-10-07): YouTube's M4A is fragmented MP4 and MediaPlayer ends it immediately over HTTP, so the app asks for WebM/Opus first. |
| f | add_history_item only needs the listed MVP endpoints | It needs **`player`** (`get_song`, body field `video_id` + `signatureTimestamp`) **and** a GET to `playbackTracking.videostatsPlaybackUrl.baseUrl` on **`s.youtube.com`** with `ver=2&c=WEB_REMIX&cpn=<16 chars>`, using the same authenticated session. It returns 204 even when nothing is recorded (#703). | Medium. Add `player` to the InnerTube client (tracking data only, not streams) and allow `s.youtube.com` in the HTTP layer. |
| g | Endpoint list is complete for the MVP | Also needed: `account/account_menu` ("signed in as"), `like/dislike` (exists), `playlist/delete`, `feedback` (library add/remove, history removal). | Low. Add them to the InnerTube interface. |
| h | "Client context: WEB_REMIX with the current client version taken from ytmusicapi" | ytmusicapi has no version constant. It computes `1.<UTC yyyyMMdd>.01.00` at construction. | Low. Implement the same formula. Optionally compare against `ytcfg.INNERTUBE_CONTEXT.client.clientVersion` from the bootstrap GET. |
| i | Cookie login: "Copy the exact logic from ytmusicapi" for SAPISIDHASH | ytmusicapi's exact logic is the **single** `SAPISIDHASH` from `__Secure-3PAPISID`, origin `https://music.youtube.com`. Browsers now send `SAPISIDHASH ... SAPISID1PHASH ... SAPISID3PHASH ...` (optionally `_u`), but single-part still works (ytmusicapi CI green on 2026-10-01). | Low. Implement single-part (the spec). Keep the yt-dlp three-part variant (4.3) documented as a fallback behind the same interface. |
| j | Cookie sessions are long-lived; the WebView2 window "closes right after login" | Reports of cookie sessions dying after hours to days (#1016 2026-09, #921), and IP binding (#962). ytmusicapi never applies `Set-Cookie` rotations in cookie mode. | Medium. Detect "signed out" responses (e.g. 401, "Sign in" message renderers) and prompt for re-login, which reopens the sign-in WebView2. Silent cookie refresh with a hidden WebView2 is ruled out: the only web view is the sign-in window. |
| k | yt-dlp sidecar with startup update check, MSIX package | `yt-dlp -U` rewrites the exe in place, and the MSIX install directory is read-only. | Medium. Copy `yt-dlp.exe` to `%LocalAppData%\...\tools\` on first run and run/update it there. Outcome: the app downloads the official release itself into `%LOCALAPPDATA%\HushMusic\tools\` and updates it there. |

---

## Appendix A: errors and gotchas worth unit-testing

- HTTP ≥ 400: the body is `{"error": {"code": 400, "message": "...", "errors": [...], "status": "INVALID_ARGUMENT"}}`. Surface `error.message` (`_send_request`). Known messages:
  - `Request contains an invalid argument.` (OAuth today)
  - `You must be signed in to perform this operation.` (missing or expired auth, from #676)
  - `Unauthorized. You must be signed in.` (#962)
- HTTP 200 but not performed: `actions[0].showEngagementPanelEndpoint` on write calls (`validate_write_response`).
- HTTP 409 or 400 when editing a playlist within about 20 s of creating it (tests).
- `get_history` when signed out: it may return an `itemSectionRenderer`/`messageRenderer` "Sign in" prompt instead of `musicShelfRenderer`. ytmusicapi then raises an unhelpful error (#1016). Treat this as "session expired".
- `next` with no queue content: likely a private or inaccessible playlist.
- Searching while authenticated writes to the account's search history.

## Appendix B: id prefixes

| Prefix | Meaning | Used with |
|---|---|---|
| `VL` + playlistId | browse id of a playlist | `browse` (added by `get_playlist`; stripped for edit/next) |
| `LM` | Liked Music playlist id (`VLLM` as browse id) | `get_liked_songs` |
| `PL...` | user/regular playlist | |
| `OLAK5uy_...` | album audio playlist (`audioPlaylistId`) | `next`, `get_playlist` |
| `MPREb_...` | album browse id | `get_album` |
| `MPLA` + channelId | artist browse id (prefix stripped by `get_artist`) | `get_artist` |
| `UC...` | channel / artist id | `get_artist` |
| `RDAMVM` + videoId | song radio | `next` playlistId |
| `RDAMPL` + playlistId | playlist/album radio | `next` playlistId |
| `MPLYt...` | lyrics browse id | `get_lyrics` |
| `MPTRt...` | related browse id | `get_song_related` |
| `MPTC...` | song credits browse id | `get_song_credits` |
| `FEmusic_*` | fixed pages: `home`, `liked_playlists`, `liked_videos`, `liked_albums`, `library_corpus_track_artists`, `library_corpus_artists`, `history` | `browse` |

The FAQ's browseId table has its "Main" column swapped for the RDAMVM / RDAMPL rows. The FAQ prose above it is correct: `RDAMVM` + videoId, `RDAMPL` + playlistId.
