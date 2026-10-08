# InnerTube parser spec (derived from ytmusicapi)

Source of truth: `sigma67/ytmusicapi` master, commit `4aeaf7d` (2026-10-01). Every path and rule below is
read from that code; places where the **live responses captured on 2026-10-07** disagree with ytmusicapi
are called out explicitly (section 13). Test data: `tests/HushMusic.InnerTube.Tests/Fixtures/*.json`,
expected values in `Fixtures/EXPECTED.md`.

## 0. Conventions

- Paths are literal key sequences: `a.b[0].c`. `[0]` = first element, `[-1]` = last element.
  Paths starting with `.` are relative to the renderer object being parsed (e.g. relative to the value of
  `musicResponsiveListItemRenderer`).
- ytmusicapi's `nav(root, path, none_if_absent)` either throws or returns `None`. In C# **every** lookup is
  null-safe: a missing *required* field (title / id) -> log a warning and skip the item; a missing optional
  field -> `null`. Never throw for the page.
- "text" of a run list = `runs[i].text`. A run "has a link" when it has a `navigationEndpoint` key.
- `DOT` = the separator run `{"text": " • "}` (U+2022 with a space each side). ytmusicapi compares the
  whole run object for equality, i.e. a run whose only key is `text` and whose text is exactly `" • "`.
- Abbreviations used only in prose: MRLIR = `musicResponsiveListItemRenderer`, MTRIR =
  `musicTwoRowItemRenderer`, MMRIR = `musicMultiRowListItemRenderer`.

## 1. Request facts that matter for parsing

From `ytmusic.py` / `helpers.py` (`_send_request`, `initialize_context`, `initialize_headers`):

- URL: `https://music.youtube.com/youtubei/v1/{endpoint}?alt=json` + optional extra query
  (continuations: `&ctoken={T}&continuation={T}`). With cookie (browser) auth ytmusicapi also appends
  `&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30`.
- Body = endpoint body merged with
  `{"context":{"client":{"clientName":"WEB_REMIX","clientVersion":"1.<UTC yyyyMMdd>.01.00","gl":"US","hl":"en"},"user":{}}}`.
  `clientVersion` is **computed from today's UTC date**, not a pinned constant.
- Headers: `user-agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0`,
  `accept: */*`, `content-type: application/json`, `origin: https://music.youtube.com`,
  `X-Goog-Visitor-Id: <ytcfg VISITOR_DATA scraped from GET https://music.youtube.com>`, cookie `SOCS=CAI`.
- Timed lyrics only: client temporarily switched to `clientName: "ANDROID_MUSIC"`, `clientVersion: "7.21.50"`
  (`YTMusicBase.as_mobile`).

**Where Hush differs from ytmusicapi on the request side** (none of it changes parsing):
1. Hush sends `?alt=json&prettyPrint=false`. ytmusicapi sends only `?alt=json`, which returns
   **pretty-printed** JSON: 118,918 bytes for a `music/get_search_suggestions` call, against 44,568 bytes
   for the same document minified. The minified form is smaller and parses identically.
2. ytmusicapi has no client-version constant. It computes `"1." + utcNow.ToString("yyyyMMdd") + ".01.00"`,
   and Hush does the same.
3. Hush has no OAuth sign-in: InnerTube rejects Bearer tokens (see
   [innertube-requests.md](innertube-requests.md) §5). For reference, ytmusicapi's device-code URL is
   `https://www.youtube.com/o/oauth2/device/code` (`constants.OAUTH_CODE_URL`).

## 2. Shared building blocks

### 2.1 Navigation constants (navigation.py) fully expanded

| Constant | Full path |
|---|---|
| `SINGLE_COLUMN_TAB + SECTION_LIST` | `contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents` |
| `TWO_COLUMN_RENDERER + TAB_CONTENT + SECTION_LIST_ITEM` | `contents.twoColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0]` |
| two-column secondary list | `contents.twoColumnBrowseResultsRenderer.secondaryContents.sectionListRenderer.contents` |
| `SECTION_LIST_CONTINUATION` | `continuationContents.sectionListContinuation` |
| `TITLE_TEXT` | `.title.runs[0].text` |
| `TITLE + NAVIGATION_BROWSE_ID` | `.title.runs[0].navigationEndpoint.browseEndpoint.browseId` |
| `TITLE + NAVIGATION_BROWSE + PAGE_TYPE` | `.title.runs[0].navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType` |
| `SUBTITLE` / `SUBTITLE2` / `SUBTITLE3` | `.subtitle.runs[0].text` / `.subtitle.runs[2].text` / `.subtitle.runs[4].text` |
| `NAVIGATION_BROWSE_ID` | `.navigationEndpoint.browseEndpoint.browseId` |
| `NAVIGATION_VIDEO_ID` | `.navigationEndpoint.watchEndpoint.videoId` |
| `NAVIGATION_PLAYLIST_ID` | `.navigationEndpoint.watchEndpoint.playlistId` |
| `NAVIGATION_WATCH_PLAYLIST_ID` | `.navigationEndpoint.watchPlaylistEndpoint.playlistId` |
| `NAVIGATION_VIDEO_TYPE` (under a watchEndpoint parent) | `.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType` |
| `PLAY_BUTTON` | `.overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer` |
| `THUMBNAIL_OVERLAY_NAVIGATION` | `.thumbnailOverlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint` |
| `THUMBNAILS` (MRLIR, headers) | `.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails` |
| `THUMBNAIL_RENDERER` (MTRIR) | `.thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails` |
| `THUMBNAIL_CROPPED` (old headers) | `.thumbnail.croppedSquareThumbnailRenderer.thumbnail.thumbnails` |
| `THUMBNAIL` (watch queue) | `.thumbnail.thumbnails` |
| `BADGE_LABEL` | `.badges[0].musicInlineBadgeRenderer.accessibilityData.accessibilityData.label` |
| `SUBTITLE_BADGE_LABEL` | `.subtitleBadges[0].musicInlineBadgeRenderer.accessibilityData.accessibilityData.label` |
| `MENU_ITEMS` | `.menu.menuRenderer.items` |
| `MENU_LIKE_STATUS` | `.menu.menuRenderer.topLevelButtons[0].likeButtonRenderer.likeStatus` |
| `MENU_PLAYLIST_ID` | `.menu.menuRenderer.items[0].menuNavigationItemRenderer.navigationEndpoint.watchPlaylistEndpoint.playlistId` |
| `MENU_SERVICE` | `menuServiceItemRenderer.serviceEndpoint` (inside one menu item) |
| `FEEDBACK_TOKEN` | `feedbackEndpoint.feedbackToken` (inside a service endpoint) |
| `CAROUSEL_TITLE` | `.header.musicCarouselShelfBasicHeaderRenderer.title.runs[0]` |
| `CAROUSEL_STRAPLINE` | `.header.musicCarouselShelfBasicHeaderRenderer.strapline.runs[0]` |
| `CARD_SHELF_TITLE` | `.header.musicCardShelfHeaderBasicRenderer.title.runs[0].text` |
| `DESCRIPTION` | `.description.runs[0].text` |
| `RESPONSIVE_HEADER` | `musicResponsiveHeaderRenderer` |
| `EDITABLE_PLAYLIST_DETAIL_HEADER` | `musicEditablePlaylistDetailHeaderRenderer` |
| `TIMESTAMPED_LYRICS` | `contents.elementRenderer.newElement.type.componentType.model.timedLyricsModel.lyricsData` |

### 2.2 MRLIR columns (`parsers/_utils.py`)

- Flex column `i`: `.flexColumns[i].musicResponsiveListItemFlexColumnRenderer`. It is "missing" (→ null) when
  `i >= flexColumns.length`, or it has no `text`, or `text` has no `runs` (a greyed-out playlist item has
  `"text": {}`).
- `get_item_text(item, i, run_index=0)` = `.flexColumns[i].musicResponsiveListItemFlexColumnRenderer.text.runs[run_index].text`.
- Fixed column `i`: `.fixedColumns[i].musicResponsiveListItemFixedColumnRenderer`; its text is
  `.text.simpleText` if present, else `.text.runs[0].text` (durations live here on album/playlist rows).

### 2.3 Run classification — `parse_song_runs(runs, skip_type_spec)` (`parsers/songs.py`)

Used for subtitles, flex column 1 (+2), watch `longBylineText`, album header subtitle.

1. If `skip_type_spec` and `runs.length > 2` and `runs[0]` has no link and `runs[0]` classifies as
   `artist` and `runs[1] == DOT` and `runs[2]` classifies as artist/duration/views/year -> drop `runs[0..1]`.
   This removes the leading type word ("Song", "Video", "Album", "Single", "EP", "Playlist"…).
2. Visit only **even** indices (odd indices are separators: `" • "`, `", "`, `" & "`). For each run
   (`parse_song_run`):
   - has link: `id = .navigationEndpoint.browseEndpoint.browseId`; if `id` starts with `MPRE` or contains
     `release_detail` -> **album** `{name: text, id}`; otherwise -> **artist** `{name: text, id}` (append).
   - no link: regex `^(\d+:)*\d+:\d+$` -> **duration** (+ `duration_seconds`); regex `^\d{4}$` -> **year**;
     `parse_views(text) != null` -> **views**; else -> **artist** `{name: text, id: null}`.
3. `parse_views(text)`: (non-Latin-script prefix stripping omitted for hl=en) must start with a digit; if it
   is ASCII with no space it is NOT views (protects artist names like "2Pac"); result = first
   space-separated token, e.g. `"52M plays"` -> `"52M"`, `"1.2B views"` -> `"1.2B"`.
4. `parse_duration(text)`: trim; split on `:`; every part must be all digits else null; seconds =
   `Σ part[k] * [1,60,3600][k]` counting from the right. `"4:36"`→276, `"1:27:54"`→5274, `" "`→null.
5. Search rows concatenate flex column 1 runs + `{"text": ""}` + flex column 2 runs before calling this,
   so the extra run keeps the even/odd parity.

`parse_artists_runs(runs)`: for `j in 0..floor(len/2)`: `{name: runs[2j].text, id: runs[2j].navigationEndpoint.browseEndpoint.browseId ?? null}`
(guard the index in C# — Python would throw on an even-length list).

### 2.4 Thumbnails

ytmusicapi never picks one; it returns the raw `[{url, width, height}]` list. In every captured fixture the
list is ascending by size (60→544, 192→1200). C#: keep the list; "largest" = max by `width*height`
(do not rely on order).

### 2.5 Flags and per-row state

| Field | Rule (relative to MRLIR unless stated) |
|---|---|
| `isExplicit` | `.badges[0].musicInlineBadgeRenderer.accessibilityData.accessibilityData.label` **exists** (MTRIR and headers: `.subtitleBadges[0]…label`). Label text is not checked. |
| `isAvailable` | `false` iff `.musicItemRendererDisplayPolicy == "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT"`. |
| `likeStatus` (playlist/album rows) | `.menu.menuRenderer.topLevelButtons[0].likeButtonRenderer.likeStatus`, read only when `PLAY_BUTTON.playNavigationEndpoint` exists and `menu` exists; values `LIKE` / `INDIFFERENT` / `DISLIKE`. |
| `likeStatus` (watch rows, album header) | find `toggleMenuServiceItemRenderer` (watch) / `toggleButtonRenderer` (album header) whose `defaultServiceEndpoint.likeEndpoint.status` exists; ytmusicapi **inverts** it: default action `LIKE` ⇒ current `INDIFFERENT`; default `INDIFFERENT` ⇒ current `LIKE` (`parse_like_status`). Anonymous responses have a `modalEndpoint` instead ⇒ watch: null, album header: `"INDIFFERENT"`. |
| `inLibrary`, `feedbackTokens`, `pinnedToListenAgain`, `listenAgainFeedbackTokens`, `feedbackToken` | `parse_song_menu_data`: for each item in `.menu.menuRenderer.items` take `toggleMenuServiceItemRenderer` or `menuServiceItemRenderer`; icon = `.defaultIcon.iconType` ?? `.icon.iconType`; `isToggled` = `.isToggled`. `BOOKMARK_BORDER`: `inLibrary=isToggled`, `feedbackTokens={add: defaultServiceEndpoint.feedbackEndpoint.feedbackToken, remove: toggledServiceEndpoint.feedbackEndpoint.feedbackToken}`. `BOOKMARK`: `inLibrary=true`, tokens swapped. `KEEP`/`KEEP_OFF`: same pattern for pin/unpin to Listen again. `REMOVE_FROM_HISTORY`: `feedbackToken = serviceEndpoint.feedbackEndpoint.feedbackToken`. (Anonymous: BOOKMARK_BORDER has a sign-in `modalEndpoint` ⇒ tokens null.) |
| `setVideoId` (owned playlist rows) | first `menuServiceItemRenderer.serviceEndpoint.playlistEditEndpoint` in `.menu.menuRenderer.items` → `.actions[0].setVideoId`; its `.actions[0].removedVideoId` is used as `videoId` for unplayable rows. |
| `creditsBrowseId` | `menuNavigationItemRenderer.navigationEndpoint.browseEndpoint.browseId` starting with `MPTC`. |
| `videoType` (playlist/album rows) | `.menu.menuRenderer.items[0].menuNavigationItemRenderer.navigationEndpoint.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType` (the "Start mix" item). Search/home rows use `PLAY_BUTTON.playNavigationEndpoint.watchEndpoint…musicVideoType`. |

### 2.6 Item type detection

**musicVideoType** (`models/content/enums.py` + parsers): `MUSIC_VIDEO_TYPE_ATV` = song (audio track),
`MUSIC_VIDEO_TYPE_OMV` = official video, `MUSIC_VIDEO_TYPE_UGC` = user video,
`MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC`, `MUSIC_VIDEO_TYPE_PODCAST_EPISODE` = episode,
`MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK` = upload. Observed but unknown to ytmusicapi:
`MUSIC_VIDEO_TYPE_SHOULDER` (playlist.json) — map to "video/unknown", never fail.

**pageType** at `…browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType`:
`MUSIC_PAGE_TYPE_ALBUM`, `MUSIC_PAGE_TYPE_AUDIOBOOK` (album-like), `MUSIC_PAGE_TYPE_ARTIST`,
`MUSIC_PAGE_TYPE_USER_CHANNEL`, `MUSIC_PAGE_TYPE_PLAYLIST`, `MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE`,
`MUSIC_PAGE_TYPE_NON_MUSIC_AUDIO_TRACK_PAGE` (episode title link), `MUSIC_PAGE_TYPE_UNKNOWN` (upload artist),
`MUSIC_PAGE_TYPE_TRACK_LYRICS`, `MUSIC_PAGE_TYPE_TRACK_RELATED`. Seen in data but not used by ytmusicapi:
`MUSIC_PAGE_TYPE_ARTIST_DISCOGRAPHY` (artist "see all" albums/singles), `MUSIC_PAGE_TYPE_TRACK_CREDITS`.

**browseId prefixes** (search fallback, `parse_search_result`): `VM`/`RD`/`VL` → playlist, `MPLA`/`UC` →
artist, `MPRE` → album, `MPSP` → podcast, `MPED` → episode. Other ids: `MPAD<channelId>` artist discography,
`MPLY…` lyrics, `MPTR…` related, `MPTC…` credits, `OLAK5uy_…` album audio playlist, `RDAMVM<videoId>` song
radio, `RDEM…` artist radio, `RDAO…` artist shuffle, `RDCLAK5uy_…` YTM curated playlist, `LM` liked songs.

### 2.7 Continuations (`continuations.py`) — three mechanisms

**A. Classic ctoken** (`get_continuations`): used by home, filtered search, watch queue, library
grids/shelves, artist "see all" grids.
- Token: `<renderer>.continuations[0].next{ctoken_path}ContinuationData.continuation` with
  `ctoken_path` = `""` normally, `"Radio"` for watch queues whose playlistId is not `PL…`/`OLA…`.
- Request: **same endpoint + same body** (incl. original browseId/query) + query
  `&ctoken={T}&continuation={T}`.
- Response: `continuationContents.{type}` where `type` = `sectionListContinuation` (home),
  `musicShelfContinuation` (search, library songs/artists), `gridContinuation` (library playlists/albums,
  artist albums), `playlistPanelContinuation` (watch). Items under `.contents` (or `.items` for grids);
  next token again at `.continuations[0].next…ContinuationData.continuation`. Stop when there is no
  `continuationContents`, no `continuations`, or the page parses to 0 items.
- Observed: home and search continuation responses **also** carry a top-level `contents` skeleton (home: a
  tab shell without `content`; search: only the "About these results" `messageRenderer`) — ignore it and
  read `continuationContents` only.

**B. 2025 continuation items** (`get_continuations_2025`): used for playlist tracks.
- Token: last element of the item list: `[-1].continuationItemRenderer.continuationEndpoint.continuationCommand.token`;
  fallback: `[-1].continuationItemRenderer.continuationEndpoint.commandExecutorCommand.commands[*]` where
  `continuationCommand.request == "CONTINUATION_REQUEST_TYPE_BROWSE"` → `continuationCommand.token`.
- Request: `browse` with body **only** `{"continuation": T}` (+context).
- Response: `onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems` (items + optional
  trailing `continuationItemRenderer`).

**C. Reload continuation** (owned-playlist suggestions only): `.continuations[0].reloadContinuationData.continuation`,
same request style as A.

## 3. Home — `BrowsingMixin.get_home` → `parsers/browsing.py:parse_mixed_content`

Request `browse {"browseId":"FEmusic_home"}`. Rows: `contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`.
Continuation: mechanism A on `….sectionListRenderer` (`continuations[0].nextContinuationData.continuation`),
response rows at `continuationContents.sectionListContinuation.contents`.

For each row:
- `musicDescriptionShelfRenderer` → `{title: .header.runs[0].text, contents: .description.runs[0].text}` (a string!).
- otherwise take the row's single value (any renderer, e.g. `musicCarouselShelfRenderer`,
  `musicImmersiveCarouselShelfRenderer`); skip if it has no `contents` (e.g. `musicTastebuilderShelfRenderer`).
  Title = `.header.musicCarouselShelfBasicHeaderRenderer.title.runs[0].text`, fallback
  `.header.musicCarouselShelfBasicHeaderRenderer.strapline.runs[0].text`. (An immersive carousel's header
  key differs, so its title becomes null in ytmusicapi.)
- each element of `.contents` (for MTRIR, pageType =
  `.title.runs[0].navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType`):

| Renderer / condition | Kind | Parser |
|---|---|---|
| MTRIR, pageType null, has `.navigationEndpoint.watchPlaylistEndpoint.playlistId` | watch playlist | `parse_watch_playlist`: `title=.title.runs[0].text`, `playlistId` = that id, `thumbnails=THUMBNAIL_RENDERER` |
| MTRIR, pageType null, has `.navigationEndpoint.watchEndpoint.videoId` | song/video | `parse_song`: title, `videoId`, `playlistId=.navigationEndpoint.watchEndpoint.playlistId`, thumbnails, + `parse_song_runs(.subtitle.runs, skip_type_spec=true)` |
| MTRIR, pageType null, neither | skip (deleted upload) | |
| MTRIR, `ALBUM` / `AUDIOBOOK` | album | `parse_album` (below) |
| MTRIR, `ARTIST` / `USER_CHANNEL` | artist | `parse_related_artist`: title, `browseId=TITLE+NAVIGATION_BROWSE_ID`, `subscribers=.subtitle.runs[0].text` split on space `[0]`, thumbnails |
| MTRIR, `PLAYLIST` | playlist | `parse_playlist` (below) |
| MTRIR, `PODCAST_SHOW_DETAIL_PAGE` | podcast | `parsers/podcasts.py:parse_podcast` |
| MTRIR, other pageType | appended as null by ytmusicapi → C#: skip + warn | |
| MRLIR | song (Quick picks) | `parse_song_flat` (below) |
| MMRIR | episode | `parse_episode` |

`parse_album` (MTRIR): `title=.title.runs[0].text`; `browseId=.title.runs[0].navigationEndpoint.browseEndpoint.browseId`;
`artists` = every `.subtitle.runs[*]` that has a link → `{id: run.navigationEndpoint.browseEndpoint.browseId, name: run.text}`;
`audioPlaylistId = .thumbnailOverlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint.watchPlaylistEndpoint.playlistId`
?? `….playNavigationEndpoint.watchEndpoint.playlistId`; thumbnails = THUMBNAIL_RENDERER; `isExplicit` =
SUBTITLE_BADGE_LABEL exists; then `subtitle.runs[0].text` numeric ⇒ `year`, else `type` (and
`subtitle.runs[2].text` numeric ⇒ `year`).

`parse_playlist` (MTRIR): `playlistId = TITLE+NAVIGATION_BROWSE_ID` with the first 2 chars (`VL`) removed;
title (may be null); thumbnails; `owned` = any `.menu.menuRenderer.items[*].menuNavigationItemRenderer.navigationEndpoint.playlistEditorEndpoint.playlistId == playlistId`;
if `.subtitle.runs` exists: `description` = concatenation of all run texts; if exactly 3 runs and
`runs[2].text` matches `\d+ ` ⇒ `count = runs[2].text.split(" ")[0]`, `author = parse_artists_runs(runs[0..1))`.

`parse_song_flat` (MRLIR): col0 = flex 0, col1 = flex 1, col2 = flex 2.
`videoId = col0.text.runs[0].navigationEndpoint.watchEndpoint.videoId` ?? `PLAY_BUTTON.playNavigationEndpoint.watchEndpoint.videoId`;
`title = col0.text.runs[0].text`; `videoType = PLAY_BUTTON.playNavigationEndpoint.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType`;
thumbnails = THUMBNAILS; isExplicit = BADGE_LABEL; `parse_song_runs(col1.text.runs, skip_type_spec=true)`
(artists, views…); `album = {name: col2.text.runs[0].text, id: col2.text.runs[0].navigationEndpoint.browseEndpoint.browseId}`
only if col2 exists and its first run has a link.

Not parsed by ytmusicapi (available if wanted): `sectionListRenderer.header.chipCloudRenderer.chips` (mood
chips), carousel "more" link `.header.musicCarouselShelfBasicHeaderRenderer.title.runs[0].navigationEndpoint.browseEndpoint`
(e.g. `FEmusic_new_releases_albums`), `.itemSize`, `.numItemsPerColumn`.

## 4. Search — `SearchMixin.search` → `parsers/search.py`

Request `search {"query": q, "params"?: get_search_params(filter, scope, ignore_spelling)}`.

| filter (scope none, spelling on) | `params` |
|---|---|
| songs | `EgWKAQIIAWoMEA4QChADEAQQCRAF` |
| videos | `EgWKAQIQAWoMEA4QChADEAQQCRAF` |
| albums | `EgWKAQIYAWoMEA4QChADEAQQCRAF` |
| artists | `EgWKAQIgAWoMEA4QChADEAQQCRAF` |
| playlists | `Eg-KAQwIABAAGAAgACgBMABqChAEEAMQCRAFEAo%3D` |
| community_playlists | `EgeKAQQoAEABagwQDhAKEAMQBBAJEAU%3D` |
| featured_playlists | `EgeKAQQoADgBagwQDhAKEAMQBBAJEAU%3D` |
| profiles / podcasts / episodes | `EgWKAQ` + `JY` / `JQ` / `JI` + `AWoMEA4QChADEAQQCRAF` |

(`%3D` is sent literally inside the JSON string, exactly as ytmusicapi does.)

Sections: if `contents.tabbedSearchResultsRenderer` exists →
`contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`
(tab index `1`/`2` only for library/uploads scope), else `contents.sectionListRenderer.contents`.
No `contents` ⇒ no results.

For each section:
- `musicCardShelfRenderer` → top result (`parse_top_result`), then its `.contents` (MRLIR list) are parsed
  with result_type null; if `.contents[0]` is a `messageRenderer` it is removed and its
  `.messageRenderer.text.runs[0].text` becomes the category for the remaining items.
- `musicShelfRenderer` → items `.contents` (skip shelf if `.contents[0]` is not MRLIR); category =
  `.title.runs[0].text`.
- `itemSectionRenderer` → items `.contents` (skip if `.contents[0]` is not MRLIR, e.g. the
  "About these results" `messageRenderer`); category null.
- With a filter: shelves whose category does not contain the translated singular filter word
  (`"song"` ⊂ `"Songs"`) are skipped; result_type = filter singular (`playlists` variants → `playlist`).
  Continuation: mechanism A on `musicShelfRenderer`, type `musicShelfContinuation`.

**Observed 2026-10 layout:** the unfiltered page is a flat list of single-item `itemSectionRenderer`s (no
"Songs"/"Videos" shelves), so every non-top result has `category = null` and type comes from the
browseId/videoType fallback below.

### 4.1 Top result — `parse_top_result(musicCardShelfRenderer)`

- `resultType` = `.subtitle.runs[0].text` lower-cased, looked up in the English list
  `album, artist, playlist, song, video, station, profile, podcast, episode`; anything else (e.g.
  "single", "ep") ⇒ `album`.
- `category` = `.header.musicCardShelfHeaderBasicRenderer.title.runs[0].text` ?? literal `"Top result"`
  (captured card has no header).
- artist: `subscribers = .subtitle.runs[2].text.split(" ")[0]`; `parse_song_runs(.title.runs)` ⇒ `artists[{name,id}]`.
- song/video: `videoId = .onTap.watchEndpoint.videoId`, `videoType = .onTap.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType`.
- song/video/album: also `title=.title.runs[0].text`, `parse_song_runs(.subtitle.runs[2..])`.
- album: `browseId = .title.runs[0].navigationEndpoint.browseEndpoint.browseId`; `playlistId` from
  `.buttons[0].buttonRenderer.command` → `.watchPlaylistEndpoint.playlistId` ?? `.watchEndpoint.playlistId`.
- playlist: `playlistId = .menu.menuRenderer.items[0].menuNavigationItemRenderer.navigationEndpoint.watchPlaylistEndpoint.playlistId`,
  `title`, `author = parse_artists_runs(.subtitle.runs[2..])`.
- episode: `videoId/videoType` from `THUMBNAIL_OVERLAY_NAVIGATION.watchEndpoint…`; `date = subtitle.runs[2].text`;
  `podcast = {id,name}` of `subtitle.runs[4]`.
- `thumbnails = .thumbnail.musicThumbnailRenderer.thumbnail.thumbnails`.
- Not parsed: `.buttons[*].buttonRenderer.command.watchPlaylistEndpoint.playlistId` for artist Shuffle
  (`RDAO…`) / Mix (`RDEM…`), `.onTap.browseEndpoint.browseId` for the artist.

### 4.2 Result rows — `parse_search_result(MRLIR, result_type, category)`

- `default_offset` = 2 when result_type is null or `album` (flex 1 then starts with `"<Type>", " • "`), else 0.
- `video_type = PLAY_BUTTON.playNavigationEndpoint.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType`.
- **Type detection when result_type is null**: if `.navigationEndpoint.browseEndpoint.browseId` exists →
  prefix table (2.6); else `video_type`: `ATV` → `song`, `PODCAST_EPISODE` → `episode`, anything else →
  `video`. Profiles (`UC…` channel ids) therefore come out as `artist` (see EXPECTED.md).
  The leading type word in flex 1 (`Song`, `Video`, `Album`, `Single`, `EP`, `Artist`, `Playlist`,
  `Profile`, `Podcast`, `Episode`) is NOT used by ytmusicapi for rows.
- Fields:

| resultType | Fields |
|---|---|
| all but artist | `title = get_item_text(0)` |
| artist | `artist = get_item_text(0)`; `shuffleId`/`radioId` from `.menu.menuRenderer.items[*].menuNavigationItemRenderer` whose `.icon.iconType` is `MUSIC_SHUFFLE` / `MIX`: `.navigationEndpoint.watchPlaylistEndpoint.playlistId` ?? `.navigationEndpoint.watchEndpoint.playlistId` |
| album | `type = get_item_text(1)` (flex1 run 0: "Album"/"Single"/"EP"); `playlistId` = `PLAY_BUTTON.playNavigationEndpoint.watchPlaylistEndpoint.playlistId` ?? `….watchEndpoint.playlistId` |
| playlist | `runs = flex1.text.runs`; `has_author = runs.length == default_offset + 3`; `itemCount` = text of run `has_author*2` split on space, kept only if word[1] == `"songs"` (→ int); `author = get_item_text(1, default_offset)` if has_author |
| station | `videoId = .navigationEndpoint.watchEndpoint.videoId`, `playlistId = .navigationEndpoint.watchEndpoint.playlistId` |
| profile | `name = get_item_text(1, 2)` |
| song | `album = null` then `parse_song_menu_data` (inLibrary/feedbackTokens/…) |
| song, video, episode | `videoId = PLAY_BUTTON.playNavigationEndpoint.watchEndpoint.videoId`; `videoType = video_type`; `isAvailable` (2.5) |
| song, video, album | `duration=null, year=null`, then `parse_song_runs(flex1.runs + [{"text":""}] + flex2.runs, skip_type_spec=true)` ⇒ artists, album, views, duration, duration_seconds, year |
| artist, album, playlist, profile, podcast | `browseId = .navigationEndpoint.browseEndpoint.browseId` (playlists keep the `VL` prefix here) |
| song, album | `isExplicit` = BADGE_LABEL exists |
| episode | `live` = `.badges[0].liveBadgeRenderer` exists; runs = `flex1.text.runs[default_offset..]`; if >1 run: `date = runs[0].text`; `podcast = {id,name}` of `runs[has_date*2]` |
| all | `thumbnails = .thumbnail.musicThumbnailRenderer.thumbnail.thumbnails` |

Observed row layouts (hl=en): filtered song flex1 = `Artist[ & Artist] • Album • 5:38`, flex2 = `1.2B plays`;
unfiltered song flex1 = `Song • Artist`, flex2 = `52M plays` (no duration); card-shelf songs flex1 = `Song • 4:01`
(no artist); album flex1 = `Album • Daft Punk • 2013`; playlist (filtered) flex1 = `Author • 3K views`.

## 5. Search suggestions — `get_search_suggestions` → `parse_search_suggestions`

Request `music/get_search_suggestions {"input": text}`. ytmusicapi reads only
`contents[0].searchSuggestionsSectionRenderer.contents[*]`:
- `historySuggestionRenderer` (signed-in history): `feedbackToken = .serviceEndpoint.feedbackEndpoint.feedbackToken`.
- else `searchSuggestionRenderer`.
- `text = .navigationEndpoint.searchEndpoint.query`; `runs = .suggestion.runs` (`[{text, bold?}]`);
  `fromHistory = feedbackToken != null`.

`contents[1].searchSuggestionsSectionRenderer.contents` = MRLIR entity suggestions (artist / songs /
videos with `navigationEndpoint.browseEndpoint` or `navigationEndpoint.watchEndpoint`) — **not parsed by
ytmusicapi**; if the UI wants them they can reuse the search row parser (needs a decision, it is outside the spec).

## 6. Album — `BrowsingMixin.get_album` → `parsers/albums.py:parse_album_header_2024` + `parse_playlist_items(is_album=True)`

Request `browse {"browseId": "MPRE…"}` (ytmusicapi rejects ids not starting with `MPRE`; an
`OLAK5uy_…` audio playlist id can be resolved with `get_album_browse_id`, which scrapes the HTML page —
avoid; prefer browseIds from API data).

Header `H = contents.twoColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0].musicResponsiveHeaderRenderer`:

| Field | Path / rule |
|---|---|
| title | `H.title.runs[0].text` |
| type | `H.subtitle.runs[0].text` ("Album", "Single", "EP") |
| thumbnails | `H.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails` |
| isExplicit | `H.subtitleBadges[0].musicInlineBadgeRenderer.accessibilityData.accessibilityData.label` exists |
| description, descriptionRuns | runs `H.description.musicDescriptionShelfRenderer.description.runs`; text = concat; each run → `{text, url?: run.navigationEndpoint.urlEndpoint.url}` |
| year (and any other run info) | `parse_song_runs(H.subtitle.runs[2..])` |
| artists | `parse_artists_runs(H.straplineTextOne.runs)` or null |
| trackCount, duration | `R = H.secondSubtitle.runs`; if `R.length > 1`: `trackCount = to_int(R[0].text)` ("13 songs"→13), `duration = R[2].text` ("1 hour, 14 minutes"); else `duration = R[0].text` |
| audioPlaylistId | first `H.buttons[*]` with `musicPlayButtonRenderer` → `.musicPlayButtonRenderer.playNavigationEndpoint.watchPlaylistEndpoint.playlistId` ?? `….watchEndpoint.playlistId` |
| likeStatus | first `H.buttons[*]` with `toggleButtonRenderer` → `.toggleButtonRenderer.defaultServiceEndpoint` → inverted likeEndpoint status (2.5); default `"INDIFFERENT"` |

Tracks: `contents.twoColumnBrowseResultsRenderer.secondaryContents.sectionListRenderer.contents[0].musicShelfRenderer.contents`
→ `parse_playlist_items(…, is_album=true)` (section 8.3) with preset columns: title = flex0, artists = flex1,
`views = get_item_text(2)` raw (e.g. `"54M plays"`, not trimmed), duration = fixed column 0,
`trackNumber = int(.index.runs[0].text)` (null when greyed out). Then ytmusicapi sets every
`track.album = album.title` (a **string**) and `track.artists = track.artists ?? album.artists`;
`album.duration_seconds = Σ track.duration_seconds`.

Other carousels: `….secondaryContents.sectionListRenderer.contents[1..]` → `.musicCarouselShelfRenderer`;
key by `.itemSize`: `COLLECTION_STYLE_ITEM_SIZE_SMALL` → `related_recommendations`,
`COLLECTION_STYLE_ITEM_SIZE_MEDIUM` → `other_versions`; items = MTRIR only, `parse_album`. (ytmusicapi
throws on any other itemSize — C#: skip.)

Legacy header `header.musicDetailHeaderRenderer` (`parse_album_header`) is only used for uploaded albums
(`mixins/uploads.py`); `parse_playlist_header` (same fallback) only for the podcasts episodes playlist.
Neither applies to the MVP pages.

## 7. Artist — `BrowsingMixin.get_artist` + `parsers/i18n.py:Parser.parse_channel_contents`

Request `browse {"browseId": channelId}` (a leading `MPLA` is stripped first).
Sections `S = contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`,
fallback `contents.twoColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`.
Header `H = header.musicImmersiveHeaderRenderer`.

| Field | Path / rule |
|---|---|
| name | `H.title.runs[0].text` |
| description, descriptionRuns, views | first `S[*].musicDescriptionShelfRenderer` → runs `.description.runs` (same run→url rule as album); `views = .subheader.runs[0].text` ("6,030,217,324 views") |
| channelId | `H.subscriptionButton.subscribeButtonRenderer.channelId` (≠ requested browseId; only for subscribe) |
| subscribers | `H.subscriptionButton.subscribeButtonRenderer.subscriberCountText.runs[0].text` |
| subscribed | `H.subscriptionButton.subscribeButtonRenderer.subscribed` |
| shuffleId | `H.playButton.buttonRenderer.navigationEndpoint.watchEndpoint.playlistId` |
| radioId | `H.startRadioButton.buttonRenderer.navigationEndpoint.watchEndpoint.playlistId` ?? `….watchPlaylistEndpoint.playlistId` |
| monthlyListeners | `H.monthlyListenerCount.runs[0].text` with the literal `" monthly audience"` removed |
| thumbnails | `H.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails` |
| songs | only if `S[0].musicShelfRenderer` exists: `browseId = S[0].musicShelfRenderer.title.runs[0].navigationEndpoint.browseEndpoint.browseId` (a `VL…` playlist → open with get_playlist); `results = parse_playlist_items(S[0].musicShelfRenderer.contents)` |

Carousel sections: for each `S[*].musicCarouselShelfRenderer` compare
`.header.musicCarouselShelfBasicHeaderRenderer.title.runs[0].text.lower()` with the English catalog:

| Output key | Title (en) | Item renderer → parser |
|---|---|---|
| albums | `albums` | MTRIR → `parse_album` |
| singles | `singles & eps` | MTRIR → `parse_single` (title, browseId, thumbnails, type/year from subtitle) |
| shows | `audiobooks and shows` | MTRIR → `parse_album` |
| videos | `videos` | MTRIR → `parse_video` |
| playlists | `playlists` | MTRIR → `parse_playlist` |
| related | `fans might also like` | MTRIR → `parse_related_artist` |
| episodes | `latest episodes` | MMRIR → `parse_episode` |
| podcasts | `podcasts` | MTRIR → `parse_podcast` |

"See all": `browseId = .header.musicCarouselShelfBasicHeaderRenderer.title.runs[0].navigationEndpoint.browseEndpoint.browseId`,
`params = ….browseEndpoint.params` — only when the title run has a link (else browseId null). The same
endpoint is duplicated in `.header.musicCarouselShelfBasicHeaderRenderer.moreContentButton.buttonRenderer.navigationEndpoint`.
- albums/singles/shows: browseId `MPAD<channelId>` + params → `get_artist_albums`.
- videos (and songs): browseId `VL…` → `get_playlist`.

`parse_video` (MTRIR): `runs = .subtitle.runs`; artists = `parse_artists_runs(runs[0..indexOf(DOT)))`;
`videoId = .navigationEndpoint.watchEndpoint.videoId` ?? first `.menu.menuRenderer.items[*].menuServiceItemRenderer.serviceEndpoint.queueAddEndpoint.queueTarget.videoId`;
`playlistId = .navigationEndpoint.watchEndpoint.playlistId`; `views = runs[-1].text.split(" ")[0]`.

Captured Daft Punk page also has `Live performances`, `Featured on`, `Playlists by Daft Punk` carousels
that ytmusicapi silently drops (title mismatch); "Fans might also like" subtitles are now
`"75.9M monthly audience"`, so `related[].subscribers` actually holds the monthly audience.

### 7.1 Artist albums ("see all") — `get_artist_albums` → `parsers/library.py:parse_albums`

Request `browse {"browseId": "MPAD…", "params": "…"}`. Items:
`contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0].gridRenderer.items`
?? `…contents[0].musicCarouselShelfRenderer.contents`. Continuation A on `gridRenderer` (`gridContinuation`).
Sort order (optional): options at `….sectionListRenderer.header.musicSideAlignedItemRenderer.endItems[0].musicSortFilterButtonRenderer.menu.musicMultiSelectMenuRenderer.options[*]`,
matched by `.musicMultiSelectMenuItemRenderer.title.runs[0].text` ("Recency", "Popularity",
"Alphabetical order"), then `.musicMultiSelectMenuItemRenderer.selectedCommand.commandExecutorCommand.commands[-1].browseSectionListReloadEndpoint.continuation.reloadContinuationData.continuation`
requested as mechanism C; result at `continuationContents.sectionListContinuation.contents[0]`.

`parse_albums` (MTRIR): `browseId = TITLE+NAVIGATION_BROWSE_ID`; `playlistId = MENU_PLAYLIST_ID`
(`.menu.menuRenderer.items[0].menuNavigationItemRenderer.navigationEndpoint.watchPlaylistEndpoint.playlistId`);
title; thumbnails (THUMBNAIL_RENDERER); if `.subtitle.runs`: `type = .subtitle.runs[0].text` and
`parse_song_runs(.subtitle.runs[2..])` (year; artists if linked runs exist).

## 8. Playlist — `PlaylistsMixin.get_playlist` → `parsers/playlists.py`

Request `browse {"browseId": "VL" + playlistId}` (no double `VL`). Liked songs = `VLLM`.

### 8.1 Header variants

`HD = contents.twoColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0]`.

| Variant | Detection | `id` | header `H` | `privacy` |
|---|---|---|---|---|
| Owned / editable | `HD.musicEditablePlaylistDetailHeaderRenderer` exists → `owned = true` | `HD.musicEditablePlaylistDetailHeaderRenderer.playlistId` | `HD.musicEditablePlaylistDetailHeaderRenderer.header.musicResponsiveHeaderRenderer` | `HD.musicEditablePlaylistDetailHeaderRenderer.editHeader.musicPlaylistEditHeaderRenderer.privacy` (`PUBLIC`/`PRIVATE`/`UNLISTED`) |
| Public / not owned | else, `owned = false` | `H.buttons[*]` first `musicPlayButtonRenderer` → `.playNavigationEndpoint.watchEndpoint.playlistId` | `HD.musicResponsiveHeaderRenderer` | `"PUBLIC"` |
| Audio playlist without header | id starts with `OLA`/`VLOLA` **and** `HD` missing → `parse_audio_playlist` | `secondaryContents.sectionListRenderer.contents[0].musicPlaylistShelfRenderer.targetId` | none: `title = tracks[0].album.name`, `trackCount = tracks.length` | `"PUBLIC"` |

Common header fields (`parse_playlist_header_meta(H)` + `get_playlist`):

| Field | Rule |
|---|---|
| description | `H.description.musicDescriptionShelfRenderer.description.runs[*].text` concatenated, else null |
| title | `H.title.runs[*].text` concatenated |
| thumbnails | `H.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails` |
| author | if `H.facepile` and NOT collaborative: `{name: H.facepile.avatarStackViewModel.text.content, id: H.facepile.avatarStackViewModel.rendererContext.commandContext.onTap.innertubeCommand.browseEndpoint.browseId}` |
| collaborators | if `H.facepile.avatarStackViewModel.rendererContext.commandContext.onTap.innertubeCommand.showEngagementPanelEndpoint.identifier.tag == "PAplaylist_collaborate"`: `{text: …rendererContext.accessibilityContext.label, avatars: H.facepile.avatarStackViewModel.avatars[*].avatarViewModel.image.sources[0]}` |
| views, trackCount, duration | `R = H.secondSubtitle.runs`; `hasViews = R.length > 3 ? 2 : 0`; `views = hasViews ? to_int(R[0].text) : null`; `hasDuration = R.length > 1 ? 2 : 0`; `duration = hasDuration ? R[hasViews+hasDuration].text : null`; `trackCount = to_int(all digits of R[hasViews].text)` (e.g. `"11K views • 151 tracks • 15+ hours"`) |
| year (etc.) | `parse_song_runs(H.subtitle.runs[2 + (owned ? 2 : 0) ..])` — owned subtitle is `Playlist • Private • 2024` |
| duration_seconds | Σ tracks' duration_seconds |

### 8.2 Tracks and continuation

`SL = contents.twoColumnBrowseResultsRenderer.secondaryContents.sectionListRenderer`;
`shelf = SL.contents[0].musicPlaylistShelfRenderer`; tracks = `parse_playlist_items(shelf.contents, is_collaborative)`
(the trailing `continuationItemRenderer` is skipped because it is not MRLIR). Continuation: mechanism B on
`shelf.contents` (token in its last element). `SL.continuations[0].nextContinuationData` is NOT the track
continuation — it loads suggestions (owned) and related playlists (`continuationContents.sectionListContinuation.contents[0].musicCarouselShelfRenderer`, parsed with `parse_playlist`).

### 8.3 Row parser — `parse_playlist_item(MRLIR, is_album, is_collaborative)`

1. Menu scan (`.menu.menuRenderer.items`): `playlistEditEndpoint` → `setVideoId` and fallback `videoId`
   (`removedVideoId`); `menuNavigationItemRenderer` browseId starting `MPTC` → `creditsBrowseId`.
2. `parse_song_menu_data` (library state; defaults `inLibrary`/`pinnedToListenAgain` = null).
3. If `PLAY_BUTTON.playNavigationEndpoint` exists: `videoId = PLAY_BUTTON.playNavigationEndpoint.watchEndpoint.videoId`
   and (if menu) `likeStatus = MENU_LIKE_STATUS`.
4. `isAvailable` (GREY_OUT rule). Preset columns when `!isAvailable || is_album`: title 0, artist 1,
   album 2 (3 if collaborative).
5. Column scan over flex columns `i`: let `ne = flex[i].text.runs[0].navigationEndpoint`.
   - no `ne`: if `runs[0].text` classifies as duration → `duration_index = i`, else first such `i` →
     `unrecognized_index`.
   - `ne.watchEndpoint` → `title_index = i`.
   - `ne.browseEndpoint` pageType: `ARTIST`/`UNKNOWN` → `artist_index`; `ALBUM`/`AUDIOBOOK` →
     `album_index`; `USER_CHANNEL` → collect; `NON_MUSIC_AUDIO_TRACK_PAGE` → `title_index`.
   - artist fallback: `unrecognized_index`, then the last USER_CHANNEL column (uploader of a UGC video).
6. `title = get_item_text(title_index)`; title `"Song deleted"` ⇒ drop row. `artists = parse_artists_runs(flex[artist_index].text.runs)`;
   `album = {name: get_item_text(album_index), id: flex[album_index].text.runs[0].navigationEndpoint.browseEndpoint.browseId}`;
   `views = get_item_text(2)` only for albums; `duration` = flex[duration_index] text if found, overridden by
   fixed column 0 when `fixedColumns` exists; `duration_seconds = parse_duration(duration)`.
7. `thumbnails = THUMBNAILS`, `isExplicit = BADGE_LABEL`, `videoType` (2.5),
   `communityVoteStatus` = `.engagementBar.engagementBarViewModel.actions[0].votingViewModel.initialState`
   → `{netVoteValue: .votes, status: .status}` (collaborative voting).
8. Album only: `trackNumber = int(.index.runs[0].text)` if available.
9. Output keys: `videoId, title, artists, album, likeStatus, inLibrary, pinnedToListenAgain, feedbackTokens?, thumbnails, isAvailable, isExplicit, videoType, views, communityVoteStatus, trackNumber?, duration?, duration_seconds?, setVideoId?, creditsBrowseId?`.

Greyed-out rows have no menu/play button ⇒ `videoId = null` (unless owned) although
`.playlistItemData.videoId` is present (not read by ytmusicapi). `.playlistItemData.playlistSetVideoId`
is also present on album/playlist rows; ytmusicapi ignores it.

## 9. Watch playlist / next — `WatchMixin.get_watch_playlist` → `parsers/watch.py`

Request `next` with body
`{"enablePersistentPlaylistPanel":true,"isAudioOnly":true,"tunerSettingValue":"AUTOMIX_SETTING_NORMAL"}` plus:
- `videoId` (if given); `playlistId` = given id with `VL` stripped, else `"RDAMVM" + videoId`;
- `watchEndpointMusicSupportedConfigs: {watchEndpointMusicConfig: {hasPersistentPlaylistPanel: true, musicVideoType: "MUSIC_VIDEO_TYPE_ATV"}}` when videoId given and not radio/shuffle;
- `params: "wAEB"` for radio, `"wAEB8gECKAE%3D"` for shuffle (radio wins).

`W = contents.singleColumnMusicWatchNextResultsRenderer.tabbedRenderer.watchNextTabbedResultsRenderer`.
- Tab browse ids (`get_tab_browse_ids`): for each `W.tabs[*].tabRenderer` without `unselectable`:
  `pageType = .endpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType`
  → `MUSIC_PAGE_TYPE_TRACK_LYRICS` ⇒ `lyrics = .endpoint.browseEndpoint.browseId` (`MPLY…`),
  `MUSIC_PAGE_TYPE_TRACK_RELATED` ⇒ `related` (`MPTR…`). A lyrics tab with `"unselectable": true` means no lyrics.
- Queue `P = W.tabs[0].tabRenderer.content.musicQueueRenderer.content.playlistPanelRenderer` (missing ⇒ error
  "No content returned", e.g. private playlist).
- `playlistId` = first non-null `P.contents[*].playlistPanelVideoRenderer.navigationEndpoint.watchEndpoint.playlistId`.
- Rows `P.contents[*]`: `playlistPanelVideoWrapperRenderer` → use `.primaryRenderer` and parse
  `.counterpart[0].counterpartRenderer.playlistPanelVideoRenderer` as `counterpart` (audio/video twin);
  skip rows without `playlistPanelVideoRenderer` or with `unplayableText`.
- `parse_watch_track(V = playlistPanelVideoRenderer)`:

| Field | Path |
|---|---|
| videoId | `V.videoId` |
| title | `V.title.runs[0].text` |
| length | `V.lengthText.runs[0].text` (no seconds in ytmusicapi output; use `parse_duration`) |
| thumbnail | `V.thumbnail.thumbnails` |
| likeStatus | `V.menu.menuRenderer.items[*].toggleMenuServiceItemRenderer.defaultServiceEndpoint.likeEndpoint.status`, inverted (2.5) |
| videoType | `V.navigationEndpoint.watchEndpoint.watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType` |
| inLibrary, feedbackTokens, pinnedToListenAgain, listenAgainFeedbackTokens | `parse_song_menu_data(V)` (defaults null) |
| artists, album, views, year | `parse_song_runs(V.longBylineText.runs)` (no type-spec skipping) e.g. `Oasis • (What's The Story) Morning Glory? • 1995` |

Not read by ytmusicapi but present: `V.playlistSetVideoId`, `V.navigationEndpoint.watchEndpoint.index`,
`V.selected`, `P.isInfinite`.
Continuation: mechanism A on `P`, type `playlistPanelContinuation`, ctoken path `Radio`
(`nextRadioContinuationData`) unless the playlistId starts with `PL`/`OLA` (`nextContinuationData`).

## 10. Lyrics — `BrowsingMixin.get_lyrics` (`models/lyrics.py`)

Plain (WEB_REMIX): `browse {"browseId": "MPLY…"}`.
- `lyrics = contents.sectionListRenderer.contents[0].musicDescriptionShelfRenderer.description.runs[0].text`
  (null ⇒ no lyrics). Line breaks are `\r\n`.
- `source` = `contents.sectionListRenderer.contents[0].musicDescriptionShelfRenderer.runs[0].text` in
  ytmusicapi — **this path does not exist** in the captured response; the text "Source: LyricFind" is at
  `….musicDescriptionShelfRenderer.footer.runs[0].text`. (Decision needed: follow ytmusicapi → null, or use footer.)
- `hasTimestamps = false`.

Timed (`timestamps=True`, client ANDROID_MUSIC 7.21.50, same body):
- `D = contents.elementRenderer.newElement.type.componentType.model.timedLyricsModel.lyricsData`;
  if `D` missing ytmusicapi falls back to the plain path above (which will not exist in a mobile response ⇒ null).
- `D.timedLyricsData` missing ⇒ null.
- If **every** `D.timedLyricsData[*]` has `cueRange` ⇒ timed: line = `{text: .lyricLine, start_time: int(.cueRange.startTimeMilliseconds), end_time: int(.cueRange.endTimeMilliseconds), id: int(.cueRange.metadata.id)}` (the ms values are JSON **strings**);
  else plain lyrics = `.lyricLine` values joined with `\n`.
- `source = D.sourceMessage`.

## 11. Library (auth required — shapes from ytmusicapi code, no fixtures)

All are `browse` requests. Common locator `get_library_contents(response, renderer)`:
1. `SL = contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`.
2. If `SL` missing (empty library): `n = contents.singleColumnBrowseResultsRenderer.tabs.length`; tab = `tabs[1]` if `n < 3` else `tabs[2]`;
   `contents.singleColumnBrowseResultsRenderer.tabs[k].tabRenderer.content.sectionListRenderer.contents[0].<renderer>`.
3. Else if some `SL[*]` has `itemSectionRenderer`: `that.itemSectionRenderer.contents[0].<renderer>`;
   else `SL[0].<renderer>`.
   (`<renderer>` = `gridRenderer` or `musicShelfRenderer`.)
Optional sort `params` (`prepare_order_params`): a_to_z `ggMGKgQIARAA`, z_to_a `ggMGKgQIARAB`,
recently_added `ggMGKgQIABAB`.

| Page | browseId | Container | Items / parser | Continuation (A) |
|---|---|---|---|---|
| Library playlists | `FEmusic_liked_playlists` | `gridRenderer` | `.items[1..]` (item 0 = "New playlist" tile) → MTRIR `parse_playlist` (same as home) | `gridContinuation` |
| Library songs | `FEmusic_liked_videos` | `musicShelfRenderer` | drop `.contents[0]` ("Shuffle all" row) when ≥2 rows; rest → `parse_playlist_items` (same as playlist rows) | `musicShelfContinuation`, rows → `parse_playlist_items` |
| Library albums | `FEmusic_liked_albums` | `gridRenderer` | `.items` → MTRIR `parse_albums` (same as artist "see all") | `gridContinuation` |
| Library artists | `FEmusic_library_corpus_track_artists` | `musicShelfRenderer` | `.contents` → MRLIR `parse_artists` | `musicShelfContinuation` |
| Subscriptions | `FEmusic_library_corpus_artists` | `musicShelfRenderer` | same `parse_artists` | same |

`parse_artists` (MRLIR): `browseId = .navigationEndpoint.browseEndpoint.browseId`; `artist = get_item_text(0)`;
`type` = `channel` / `artist` from `.navigationEndpoint.browseEndpoint.browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType`
(`USER_CHANNEL` / `ARTIST`); `shuffleId/radioId` via menu icons (as search artists);
`subscribers = get_item_text(1).split(" ")[0]` if present; `thumbnails = THUMBNAILS`.

### 11.1 Liked songs — `get_liked_songs` = `get_playlist("LM")`

`browse {"browseId":"VLLM"}` → exactly the playlist parser (section 8): header + `musicPlaylistShelfRenderer`
rows + mechanism-B continuation. ytmusicapi notes that suggestions/related are absent there.
Note: "Library songs" (`FEmusic_liked_videos`, saved to library) ≠ "Liked songs" (`LM`, thumbs-up).

### 11.2 History — `get_history`

`browse {"browseId":"FEmusic_history"}`; sections `contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`.
Each section must have `.musicShelfRenderer.contents` (else ytmusicapi raises with
`.musicNotifierShelfRenderer.title.runs[0]` as the error). Rows → `parse_playlist_items`; every song gets
`played = .musicShelfRenderer.title.runs[0].text` ("Today", "Yesterday", …) and, from its menu,
`feedbackToken` (icon `REMOVE_FROM_HISTORY`) used by `remove_history_items` (`feedback` endpoint,
`{"feedbackTokens":[…]}`). No continuation handling in ytmusicapi.

Reporting plays (`add_history_item`): GET
`player` response `playbackTracking.videostatsPlaybackUrl.baseUrl` with query `ver=2`, `c=WEB_REMIX`,
`cpn=<16 random chars from A-Za-z0-9-_>`. The `player` request is `{"playbackContext":{"contentPlaybackContext":{"signatureTimestamp": <days since epoch - 1>}},"video_id": videoId}` (`get_song`).

## 12. Localized-text dependencies (we fix `hl=en`, but these break if YouTube rewords)

| Where | Dependency |
|---|---|
| `parse_channel_contents` (artist) | carousel title must equal (case-insensitive) `albums`, `singles & eps`, `audiobooks and shows`, `videos`, `playlists`, `fans might also like`, `latest episodes`, `podcasts`. "Playlists by Daft Punk" / "Featured on" / "Live performances" are already dropped. |
| `get_artist` | `monthlyListeners` strips the literal `" monthly audience"`. |
| `parse_top_result` | `subtitle.runs[0].text` lower-cased compared to `album, artist, playlist, song, video, station, profile, podcast, episode`; unknown ⇒ album. Default category literal `"Top result"`. |
| `search` filtered | shelf kept only if its title contains `song`/`video`/`album`/`artist`/`playlist`/`profile`/`podcast`/`episode` (singular filter word). |
| `parse_search_result` playlist | `itemCount` only when the word after the number is exactly `songs` (now always null: YT shows "views"). |
| `parse_playlist_item` | row dropped when title == `"Song deleted"`. |
| `get_artist_albums(order=…)` | sort option titles `Recency`, `Popularity`, `Alphabetical order`. |
| `get_song_credits` | section titles `Performed by`, `Written by`, `Produced by`, `Music metadata provided by`. |
| positional/number-first text | `subscribers`/`views`/`count` = first space token (`"7.19M subscribers"`→`7.19M`); `parse_views` expects digit-first; `to_int` strips all non-digits (`"11K views"`→11). |
| pass-through display text | album `type` ("Album"/"Single"/"EP"), album/playlist `duration` ("1 hour, 14 minutes", "15+ hours"), history `played`, lyrics `source`. |

Language-independent (prefer these when extending): pageType, musicVideoType, browseId prefixes,
icon types (`MUSIC_SHUFFLE`, `MIX`, `BOOKMARK_BORDER`, `KEEP`, `REMOVE_FROM_HISTORY`), itemSize,
`musicItemRendererDisplayPolicy`.

## 13. Captured data vs ytmusicapi — observed deviations (2026-10-07)

1. Plain lyrics `source` path is wrong in ytmusicapi (null); real text at `musicDescriptionShelfRenderer.footer.runs[0].text`.
2. Playlist header views: `"11K views"` → `to_int` → `11` (magnitude lost).
3. Unfiltered search is a flat `itemSectionRenderer` list → all categories null, profiles typed as `artist`.
4. Search playlist `itemCount` always null; artist `related[].subscribers` holds monthly audience.
5. In album.json (anonymous) the track rows carry **video (OMV) videoIds** (`MUSIC_VIDEO_TYPE_OMV`); the
   song (ATV) id seen in search only appears inside `creditsBrowseId = "MPTC" + atvVideoId`
   (e.g. `IluRBvnYMoY` vs `MPTCzKSsP2084nU`). A single can list two tracks with the same videoId
   (album_single.json). Key rows by index/`playlistSetVideoId`, not videoId.
6. `MUSIC_VIDEO_TYPE_SHOULDER` appears; not in ytmusicapi's enum.
7. Continuation responses for home/search carry a leftover top-level `contents` skeleton next to
   `continuationContents`.

## 14. Model suggestions (fields ytmusicapi returns)

```text
ArtistRef        { Name, Id? }                               // Id null for unlinked runs
AlbumRef         { Name, Id? }
Thumbnail        { Url, Width, Height }
LikeStatus       enum { Like, Indifferent, Dislike }         // nullable where ytmusicapi returns None
VideoType        enum { Atv, Omv, Ugc, OfficialSourceMusic, PodcastEpisode, PrivatelyOwnedTrack, Unknown } + RawValue
LibraryTokens    { Add?, Remove? }                           // feedbackTokens

Track            { VideoId?, Title, Artists[], Album: AlbumRef?, DurationText?, DurationSeconds?,
                   Thumbnails[], IsExplicit, IsAvailable, VideoType?, LikeStatus?, InLibrary?,
                   FeedbackTokens?, PinnedToListenAgain?, ListenAgainTokens?, Views?, Year?,
                   SetVideoId?, CreditsBrowseId?, TrackNumber?, CommunityVote?,
                   FeedbackToken? /*history remove*/, Played? /*history group*/ }
AlbumSummary     { BrowseId, Title, Type?, Year?, Artists[], AudioPlaylistId?, IsExplicit, Thumbnails[] }
Album            { BrowseId, Title, Type, Year?, Artists[]?, IsExplicit, Description, DescriptionRuns[{Text, Url?}],
                   TrackCount?, DurationText, DurationSeconds, AudioPlaylistId?, LikeStatus, Thumbnails[],
                   Tracks[], OtherVersions[AlbumSummary], RelatedRecommendations[AlbumSummary] }
ArtistSummary    { BrowseId, Name, Subscribers?, Thumbnails[], ShuffleId?, RadioId?, Kind? /*artist|channel*/ }
ArtistSection<T> { BrowseId?, Params?, Results[T] }
Artist           { BrowseId /*requested*/, ChannelId, Name, Description?, DescriptionRuns[], Views?,
                   Subscribers?, MonthlyListeners?, Subscribed, ShuffleId?, RadioId?, Thumbnails[],
                   Songs: ArtistSection<Track>?, Albums/Singles/Shows: ArtistSection<AlbumSummary>?,
                   Videos: ArtistSection<VideoSummary>?, Playlists: ArtistSection<PlaylistSummary>?,
                   Related: ArtistSection<ArtistSummary>? }
VideoSummary     { VideoId, Title, Artists[], PlaylistId?, Views?, Thumbnails[] }
PlaylistSummary  { PlaylistId /*no VL*/, Title?, Description?, Count?, Author[]?, Owned, Thumbnails[] }
Playlist         { Id, Title, Owned, Privacy, Description?, Author?: ArtistRef, Collaborators?, Year?,
                   Views?, DurationText?, TrackCount?, DurationSeconds, Thumbnails[], Tracks[],
                   NextContinuation? /*mechanism B token*/, Related[PlaylistSummary], Suggestions[Track] }
HomeShelf        { Title?, Items[HomeItem], NextContinuation? /*on the page, not per shelf*/ }
HomeItem         = Track(Song/Video) | AlbumSummary | ArtistSummary | PlaylistSummary
                   | WatchPlaylist{ PlaylistId, Title, Thumbnails[] } | Podcast | Episode
SearchResult     { Category?, ResultType, ...per-type fields of 4.1/4.2 } -> model as a discriminated union:
                   SongResult(Track + Album), VideoResult(Track), AlbumResult(AlbumSummary + PlaylistId),
                   ArtistResult(ArtistSummary), PlaylistResult(BrowseId, Title, Author?, ItemCount?),
                   ProfileResult, PodcastResult, EpisodeResult, TopResult(wraps one of these + Subscribers?)
SearchPage       { Results[SearchResult], NextContinuation? /*filtered only*/ }
SearchSuggestion { Text, Runs[{Text, Bold}], FromHistory, FeedbackToken? }
WatchTrack       { VideoId, Title, Length?, Thumbnail[], LikeStatus?, VideoType?, Artists[], Album?, Year?,
                   Views?, InLibrary?, FeedbackTokens?, PinnedToListenAgain?, Counterpart?: WatchTrack }
WatchPlaylist    { Tracks[WatchTrack], PlaylistId?, LyricsBrowseId?, RelatedBrowseId?, NextContinuation? }
LyricLine        { Text, StartMs, EndMs, Id }
Lyrics           { HasTimestamps, Text? /*plain*/, Lines[LyricLine]? /*timed*/, Source? }
```

Keep `NextContinuation` (token + mechanism) on the page models so feature code can page without knowing
InnerTube details.
