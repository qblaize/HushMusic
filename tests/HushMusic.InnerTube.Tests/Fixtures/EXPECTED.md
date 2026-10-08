# Fixture expectations

Raw InnerTube responses captured anonymously (no auth) on 2026-10-07 with ytmusicapi master `4aeaf7d` (`YTMusic(language="en", location="US")`). Saved minified UTF-8; every `visitorData` value and the `visitor_data` tracking param are replaced by `"REDACTED"`.

Every value below was produced by **ytmusicapi's own high-level function run against the saved file** (its `_send_request` was mocked to return the fixture, and the request it tried to send was checked to be byte-identical to the captured request). So a C# parser that follows ytmusicapi must produce the same values from the same file.

Conventions: `null` = ytmusicapi returned `None`. Paths like `tracks[0].title` refer to the ytmusicapi output dict, which maps 1:1 to the C# model fields suggested in `docs/parsers-spec.md`. Duration seconds are `parse_duration()` of the text (`"4:36"` -> 276).

| Fixture | Bytes | ytmusicapi call |
|---|---:|---|
| `home.json` | 201,271 | `get_home()` (first page) |
| `home_continuation.json` | 304,512 | `get_home(limit=3)` -> 1st `get_continuations(..., 'sectionListContinuation')` |
| `search_all.json` | 241,767 | `search('daft punk')` |
| `search_songs.json` | 200,818 | `search('daft punk', filter='songs')` |
| `search_songs_continuation.json` | 200,165 | `search('daft punk', filter='songs', limit=21)` -> 1st `musicShelfContinuation` |
| `search_albums.json` | 182,052 | `search('daft punk', filter='albums')` |
| `search_artists.json` | 27,488 | `search('daft punk', filter='artists')` |
| `search_playlists.json` | 168,907 | `search('daft punk', filter='playlists')` |
| `search_suggestions.json` | 44,668 | `get_search_suggestions('daft p')` |
| `album.json` | 255,295 | `get_album('MPREb_K8qWMWVqXGi')` |
| `album_single.json` | 129,634 | `get_album('MPREb_X1DQ1j0PPrX')` |
| `artist.json` | 574,107 | `get_artist('UCRr1xG_2WIDs18a6cIiCxeA')` |
| `artist_small.json` | 225,284 | `get_artist('UCLZ7tlKC06ResyDmEStSrOw')` |
| `artist_albums.json` | 238,691 | `get_artist_albums('MPADUCRr1xG_2WIDs18a6cIiCxeA', 'ggMIegYIAhoCAQI%3D')` |
| `playlist.json` | 1,087,909 | `get_playlist('PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN')` (first page) |
| `playlist_continuation.json` | 577,489 | same call -> 1st `get_continuations_2025` page |
| `watch_playlist.json` | 575,646 | `get_watch_playlist('hpSrLjc5SMs')` |
| `watch_radio.json` | 575,732 | `get_watch_playlist('hpSrLjc5SMs', radio=True)` |
| `lyrics.json` | 2,738 | `get_lyrics('MPLYt_PITqkpE6ExP-3')` |
| `lyrics_timed.json` | 624,451 | `get_lyrics('MPLYt_PITqkpE6ExP-3', timestamps=True)` |
| `lyrics_timed_unsynced.json` | 619,020 | `get_lyrics('MPLYt_OZf7JU9TYTI-4', timestamps=True)` |
| `related.json` | 683,324 | `get_song_related('MPTRt_PITqkpE6ExP-3')` |
| `player.json` | 2,749 | `get_song('hpSrLjc5SMs')` (response trimmed, see below) |
| `player_error.json` | 337 | `get_song('xxxxxxxxxxx')` (no such video; trimmed) |
| `player_login_required.json` | 1,760 | `get_song('6kLq3WMV1nU')` (age-restricted; trimmed) |

## home.json

- Request: `POST browse` body `{"browseId": "FEmusic_home"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `parse_mixed_content(contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents)`
- Assertions:
  1. Raw `sectionListRenderer.contents` has 3 entries: ["musicCarouselShelfRenderer", "musicCarouselShelfRenderer", "musicTastebuilderShelfRenderer"]; parsed shelves = **2** (`musicTastebuilderShelfRenderer` has no `contents` and is skipped).
  2. Shelf[0] title `"The sound of autumn"`, 10 items; item[0] is a playlist (`MUSIC_PAGE_TYPE_PLAYLIST`): title `"Classical for Autumn"`, playlistId `RDCLAK5uy_nxpDij9qedpxoq-Fuzu_EC1PcFNB3txpY` (browseId with leading `VL` stripped), description `"Johann Sebastian Bach, Claude Debussy, Antonio Vivaldi"`.
  3. Shelf[1] title `"Morning boost"`, 10 items; item[0] is a playlist (`MUSIC_PAGE_TYPE_PLAYLIST`): title `"Feel-Good Pop & Rock"`, playlistId `RDCLAK5uy_m0wlRoNn5iCTTgBedfoOQ19Jq9P3XTLIA` (browseId with leading `VL` stripped), description `"Ed Sheeran, Imagine Dragons, 5 Seconds of Summer, Twenty One Pilots"`.
  4. `sectionListRenderer.continuations[0].nextContinuationData.continuation` is present (length 398); it is the token used for `home_continuation.json`.
  5. Shelf[0] title comes from `header.musicCarouselShelfBasicHeaderRenderer.title.runs[0].text` (this header also has a `strapline`).

## home_continuation.json

- Request: `POST browse` body `{"browseId": "FEmusic_home"}` + query `&ctoken=<token>&continuation=<token>` (token = previous page's `nextContinuationData.continuation`) — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `parse_mixed_content(continuationContents.sectionListContinuation.contents)`
- Assertions:
  1. 3 shelves: ["New releases", "Quick picks", "Featured playlists for you"] with item counts [10, 9, 5].
  2. `New releases`[0] is an album (`MUSIC_PAGE_TYPE_ALBUM`): title `"Baiat de calitate"`, browseId `MPREb_5wKw5tSD0b4`, audioPlaylistId `OLAK5uy_nso38RBAF9tirX0yMD7gWbA8ROpAb4bUM`, type `"Single"`, artists [{"id": "UCAkTuOHJleyE4lk-n_taf9A", "name": "Nicolae Guta"}], isExplicit false.
  3. `Quick picks` uses `musicResponsiveListItemRenderer` (`parse_song_flat`): 9 items; item[0] title `"Patient Zero"`, videoId `V-uIp-WuD60`, videoType `MUSIC_VIDEO_TYPE_ATV`, artists [{"name": "Taylor Swift", "id": "UCPC0L1d253x-KuMNwa05TpA"}], album {"name": "The Life of a Showgirl: The Encore", "id": "MPREb_L64BnYDQpHZ"}, views `"19M"`.
  4. `Featured playlists for you`[0]: title `"Trending 20 Romania"`, playlistId `OLAK5uy_lcrATJDe23r1ypBltst3R6P7UTJSPdNas`, description `"Chart • YouTube Charts"`.
  5. `continuationContents.sectionListContinuation` has **no** `continuations` key (end of the anonymous home feed). The response also has a top-level `contents` that is only an empty tab shell (`singleColumnBrowseResultsRenderer.tabs[0].tabRenderer` without `content`) — parse only `continuationContents`.

## search_all.json

- Request: `POST search` body `{"query": "daft punk"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `SearchMixin.search` loop over `contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents`; `parse_top_result` for `musicCardShelfRenderer`, `parse_search_results` for each `itemSectionRenderer`/`musicShelfRenderer`.
- Assertions:
  1. Raw section list: 31 entries {"itemSectionRenderer": 30, "musicCardShelfRenderer": 1} — the unfiltered page is now a FLAT list of single-item `itemSectionRenderer`s with no shelf titles (entry[0] is a `messageRenderer` "About these results" and is skipped).
  2. 33 parsed results. results[0] = top result: category `"Top result"`, resultType `artist`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}], subscribers `"79.6M"`.
  3. results[1..3] come from `musicCardShelfRenderer.contents` (songs, category null, no artists key): [["Face to Face", "qXI87eMP-bs", "4:01", 241, "56M"], ["Aerodynamic", "52fNscjkST4", "3:33", 213, "76M"], ["Touch (feat. Paul Williams)", "RRMbhEdmhYw", "8:19", 499, "35M"]].
  4. resultType histogram: {"artist": 7, "song": 8, "album": 3, "playlist": 6, "video": 3, "episode": 3, "podcast": 3}; every result except [0] has category null.
  5. First flat song: title `"Voyager"`, videoId `OWiVJMgms9E`, videoType `MUSIC_VIDEO_TYPE_ATV`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}], views `52M`, duration null (no duration column in this layout).
  6. First album: title `"Human After All (Medley)"`, type `Single`, year `2005`, browseId `MPREb_0TDFwaZe4b2`, playlistId `OLAK5uy_nIdq0JtBX7X--kFx8nJ262FPCoyabpm5g`. Profiles are reported as resultType `artist` (browseId prefix `UC`), e.g. [["un weon fan de Daft Punk ", "UCOfqxviw4fw5DoNkWJDZAlw"], ["Retrobeatzz", "UCcRb3nvdoEUj7NLSPn0CNYw"]].

## search_songs.json

- Request: `POST search` body `{"query": "daft punk", "params": "EgWKAQIIAWoMEA4QChADEAQQCRAF"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: as search_all, with `filter='songs'` -> result_type `song`, category = shelf title.
- Assertions:
  1. Section list = [`itemSectionRenderer` (messageRenderer, skipped), `musicShelfRenderer`]; shelf title `Songs`.
  2. 20 results, all resultType `song`, category `Songs`.
  3. results[0]: title `"Instant Crush (feat. Julian Casablancas)"`, videoId `khnokW3Mw24`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}, {"name": "Julian Casablancas", "id": "UCWhpbdGLnR8XtXLr0yl2nYA"}], album {"name": "Random Access Memories", "id": "MPREb_K8qWMWVqXGi"}.
  4. results[0]: duration `5:38`, duration_seconds 338, views `1.2B`, isExplicit false, videoType `MUSIC_VIDEO_TYPE_ATV`.
  5. `musicShelfRenderer.continuations[0].nextContinuationData.continuation` present (token for `search_songs_continuation.json`).

## search_songs_continuation.json

- Request: `POST search` body `{"query": "daft punk", "params": "EgWKAQIIAWoMEA4QChADEAQQCRAF"}` + query `&ctoken=<token>&continuation=<token>` (token = previous page's `nextContinuationData.continuation`) — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_continuations(..., 'musicShelfContinuation', ...)` -> `parse_search_results(continuationContents.musicShelfContinuation.contents, 'song', 'Songs')`
- Assertions:
  1. `continuationContents.musicShelfContinuation.contents` has 20 items -> 20 songs.
  2. First: title `"Sea of Simulation"`, videoId `_bz8rU7LdyE`, album {"name": "TRON: Legacy - The Complete Edition (Original Motion Picture Soundtrack)", "id": "MPREb_3L0yrk0Vxg5"}, duration `2:41` (161 s), views `2M`.
  3. `musicShelfContinuation.continuations[0].nextContinuationData.continuation` present (more pages).
  4. `search(..., filter='songs', limit=21)` over search_songs + this file returns 40 results.
  5. Response also has a top-level `contents.sectionListRenderer` holding only the "About these results" `messageRenderer` — ignore it, read `continuationContents`.

## search_albums.json

- Request: `POST search` body `{"query": "daft punk", "params": "EgWKAQIYAWoMEA4QChADEAQQCRAF"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Assertions:
  1. 20 results, all resultType `album`, category `Albums`; type histogram {"Album": 1, "Single": 19}.
  2. results[0]: title `"Random Access Memories"`, type `Album`, year `2013`, browseId `MPREb_K8qWMWVqXGi`, playlistId `OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}].
  3. Explicit albums (`badges[0].musicInlineBadgeRenderer` present): [["Lucky DAFT PUNK", "MPREb_lZBXOfVv2Tl"], ["rx7 daft punk", "MPREb_BAuNX37AYCB"]].
  4. An artist run without navigationEndpoint yields id null: results[1].artists = [{"name": "ZY", "id": null}].
  5. `duration` is null for every album (albums carry no duration in search).

## search_artists.json

- Request: `POST search` body `{"query": "daft punk", "params": "EgWKAQIgAWoMEA4QChADEAQQCRAF"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Assertions:
  1. 8 results, all resultType `artist`, category `Artists`; the shelf has NO `continuations`.
  2. results[0]: artist `Daft Punk`, browseId `UCRr1xG_2WIDs18a6cIiCxeA`, shuffleId `RDAOni3jl65KF37F9JYsJM8DGg`, radioId `RDEMni3jl65KF37F9JYsJM8DGg` (from menu items with icon MUSIC_SHUFFLE / MIX).
  3. Artist names in order: ["Daft Punk", "The Weeknd", "The Strokes", "Pharrell", "Pentatonix", "Daft Punk's Karaoke Band", "Thomas Bangalter", "LCD Soundsystem"].

## search_playlists.json

- Request: `POST search` body `{"query": "daft punk", "params": "Eg-KAQwIABAAGAAgACgBMABqChAEEAMQCRAFEAo%3D"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Assertions:
  1. 20 results, all resultType `playlist`, category `Community playlists`.
  2. results[0]: title `"Daft Punk Slow Songs"`, author `"Victor Reuss"`, browseId `VLPLtT-b3qWIM-HP1gmNV1mZ2uUribKyo8qZ` (keeps `VL` prefix in search), itemCount null.
  3. itemCount is null for all 20 results (YT now shows `"<n> views"`, ytmusicapi only accepts `"<n> songs"`).

## search_suggestions.json

- Request: `POST music/get_search_suggestions` body `{"input": "daft p"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `parse_search_suggestions` (only `contents[0].searchSuggestionsSectionRenderer.contents`).
- Assertions:
  1. 6 text suggestions: ["daft punk", "daft punk veridis quo", "daft punk get lucky", "daft punk giorgio moroder", "daft punk one more time", "daft punk around the world"].
  2. detailed_runs=True, [0]: runs [{"text": "daft p", "bold": true}, {"text": "unk"}], fromHistory false, feedbackToken null.
  3. Raw `contents` has 2 sections; section[1] holds 5 `musicResponsiveListItemRenderer` entity suggestions (artist/songs/videos) which ytmusicapi does NOT parse.

## album.json

- Request: `POST browse` body `{"browseId": "MPREb_K8qWMWVqXGi"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_album` = `parse_album_header_2024` + `parse_playlist_items(..., is_album=True)` + secondary carousels.
- Assertions:
  1. title `"Random Access Memories"`, type `Album`, year `2013`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}], isExplicit false.
  2. trackCount 13, duration `"1 hour, 14 minutes"`, len(tracks) 13, duration_seconds (sum) 4483.
  3. audioPlaylistId `OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c`, likeStatus `INDIFFERENT`, thumbnails 4 (largest 544x544).
  4. tracks[0]: trackNumber 1, title `"Give Life Back to Music"`, videoId `IluRBvnYMoY`, duration `4:36` (276 s), views `54M plays` (raw text, not trimmed), videoType `MUSIC_VIDEO_TYPE_OMV`, creditsBrowseId `MPTCzKSsP2084nU`, album `"Random Access Memories"` (string, set from header).
  5. tracks[12]: trackNumber 13, title `"Contact"`, videoId `ZbbTmR6Xaag`, duration_seconds 384.
  6. other_versions: 1 (first `"Random Access Memories (10th Anniversary Edition)"` MPREb_eMBjPmWySjR); related_recommendations: 10.

## album_single.json

- Request: `POST browse` body `{"browseId": "MPREb_X1DQ1j0PPrX"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US` (browseId = first item of the artist's `Singles & EPs` carousel)
- Assertions:
  1. title `"GLBTM (Studio Outtakes)"`, type `Single`, year `2023`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}].
  2. trackCount 3, duration `13 minutes, 37 seconds`, duration_seconds 817, audioPlaylistId `OLAK5uy_k29fGfk85tfMcdDEJPdinu2E9VgdHuCIU`.
  3. tracks (trackNumber, title, videoId, duration): [[1, "GLBTM (Studio Outtakes)", "YiZfLvLU5Jc", "6:22"], [2, "GLBTM (Studio Outtakes) [Edit]", "YiZfLvLU5Jc", "2:39"], [3, "Give Life Back to Music", "IluRBvnYMoY", "4:36"]].
  4. tracks[0] and tracks[1] have the SAME videoId -> never key album tracks by videoId (use index / playlistItemData.playlistSetVideoId).
  5. No `other_versions` key; `related_recommendations` has 10 items.

## artist.json

- Request: `POST browse` body `{"browseId": "UCRr1xG_2WIDs18a6cIiCxeA"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_artist` (header `musicImmersiveHeaderRenderer`) + `Parser.parse_channel_contents` (English carousel-title matching).
- Assertions:
  1. name `Daft Punk`, channelId `UC_kRDKYrUlrbtrSiyu5Tflg` (differs from the requested browseId), subscribers `7.19M`, monthlyListeners `79.6M`, views `"6,030,217,324 views"`, subscribed false.
  2. shuffleId `RDAOni3jl65KF37F9JYsJM8DGg`, radioId `RDEMni3jl65KF37F9JYsJM8DGg`.
  3. songs.browseId `VLOLAK5uy_lNVBcjNtiCwq-n95uoxJ-Gd4vsfMkyZNs`, 5 songs; [0] title `"Instant Crush (feat. Julian Casablancas)"`, videoId `khnokW3Mw24`, artists [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}, {"name": "Julian Casablancas", "id": "UCWhpbdGLnR8XtXLr0yl2nYA"}], album {"name": "Random Access Memories", "id": "MPREb_K8qWMWVqXGi"}.
  4. albums: browseId null (carousel title has no link), 10 results, [0] `"Random Access Memories (Drumless Edition)"` MPREb_OgeEnoHTsCm year `2023`; singles: browseId `MPADUCRr1xG_2WIDs18a6cIiCxeA`, params `ggMIegYIAhoCAQI%3D`, 10 results.
  5. videos: browseId `VLOLAK5uy_mvGpNqcAglCR8WbCLLHnIitRBdLjho0ZI`, 10 results, [0] title `"Instant Crush (feat. Julian Casablancas)"`, videoId `a5uQMwRMHcs`, views `901M`; related: 10 results, [0] `Gorillaz` UCNIV5B_aJnLrKDSnW_MOmcQ subscribers `75.9M`.
  6. Output keys: ["description", "descriptionRuns", "views", "name", "channelId", "shuffleId", "radioId", "subscribers", "monthlyListeners", "subscribed", "thumbnails", "songs", "albums", "singles", "videos", "related"]. Carousels present but NOT parsed by ytmusicapi: ["Live performances", "Featured on", "Playlists by Daft Punk"].

## artist_small.json

- Request: `POST browse` body `{"browseId": "UCLZ7tlKC06ResyDmEStSrOw"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Assertions:
  1. name `Дружки`, channelId `UCLZ7tlKC06ResyDmEStSrOw`, subscribers `381`, monthlyListeners null, views null, description `null`.
  2. Keys: ["description", "descriptionRuns", "views", "name", "channelId", "shuffleId", "radioId", "subscribers", "monthlyListeners", "subscribed", "thumbnails", "songs", "albums", "singles", "videos"] — no `related`.
  3. albums: browseId `MPADUCLZ7tlKC06ResyDmEStSrOw`, params `ggMIegYIARoCAQI%3D`, 10 results; [0] `"Герасим & Высочин: Песни оттуда"` year `2020`, no `type` key.
  4. singles: browseId null, 1 result: `"Gerasim & Kuchumov: Yellow Blue Bus"` MPREb_IuYaJp7imG5 type `EP` year `2018`.
  5. songs: browseId `VLOLAK5uy_m3TbjGwBH-BrYM-lmEOdzy4XLFqwAJgd4`, 5 results, [0].views null.

## artist_albums.json

- Request: `POST browse` body `{"browseId": "MPADUCRr1xG_2WIDs18a6cIiCxeA", "params": "ggMIegYIAhoCAQI%3D"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US` (browseId+params from artist.json `Singles & EPs` title link)
- Parser: `get_artist_albums` -> `parse_albums(contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents[0].gridRenderer.items)`
- Assertions:
  1. 27 items; `gridRenderer` has no `continuations`.
  2. [0]: {"browseId": "MPREb_X1DQ1j0PPrX", "playlistId": "OLAK5uy_k29fGfk85tfMcdDEJPdinu2E9VgdHuCIU", "title": "GLBTM (Studio Outtakes)", "type": "Single", "year": "2023"}.
  3. [-1]: {"browseId": "MPREb_TP971moS91e", "playlistId": "OLAK5uy_mDMbRmjn5kvZIutsNwYHLHSz9MxuTRO-s", "title": "Human After All (Remixes)", "type": "EP", "year": "2005"}.
  4. type histogram {"Single": 24, "EP": 3}; no item has an `artists` key.

## playlist.json

- Request: `POST browse` body `{"browseId": "VLPLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_playlist` (non-owned header `musicResponsiveHeaderRenderer` + `parse_playlist_header_meta`, tracks via `parse_playlist_items`).
- Assertions (header + first page only):
  1. id `PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN`, title `"All Daft Punk Songs In Order"`, owned false, privacy `PUBLIC`, author {"name": "IffyAlex", "id": "UClff13Re9ULoAp1ZmjlWSPA"}, year `2023`.
  2. trackCount 151 (from `"151 tracks"`), duration `15+ hours`, views 11 (from `"11K views"` — ytmusicapi's `to_int` drops the K).
  3. description starts with `"All of daft punks songs (lives included)"`; thumbnails [[192, 192], [576, 576], [1200, 1200]].
  4. First page: musicPlaylistShelfRenderer.contents has 101 entries = 100 tracks + 1 trailing `continuationItemRenderer` (token at `continuationEndpoint.continuationCommand.token`).
  5. tracks[0]: {"videoId": "D2i0skatDaE", "title": "DAFT PUNK -THE NEW WAVE", "artists": [{"name": "Tockyn", "id": "UC1qGtCPZ93T1AmfIFtH3Q-A"}], "album": null, "isAvailable": true, "videoType": "MUSIC_VIDEO_TYPE_UGC", "duration": "7:18", "duration_seconds": 438}.
  6. tracks[99]: title `"Nocturne"`, videoId `-YQALJGxFsM`, album {"name": "TRON: Legacy - The Complete Edition (Original Motion Picture Soundtrack)", "id": "MPREb_3L0yrk0Vxg5"}, videoType `MUSIC_VIDEO_TYPE_ATV`, creditsBrowseId `MPTC-YQALJGxFsM`. Explicit tracks in whole playlist: [[46, "Aerodynamic (Slum Village Remix)", "ZEK8w1qV4x8"]]. Multi-artist: tracks[84].artists = [{"name": "Daft Punk", "id": "UCRr1xG_2WIDs18a6cIiCxeA"}, {"name": "Gabrielle", "id": "UC9ekV9MwdaKpQ_1dnoeePyA"}].

## playlist_continuation.json

- Request: `POST browse` body `{"continuation": "4qmFsgKHARIkVkxQTHdfOEk3ajZfUUZvZ2NORkEtWmd3bkR6N1g4cnZuVlVOGjplaDVRVkRwRFIxRnBSVVJSTlU1RWJFTlBWVkYzVDBSTk0xRlZSVEZSYWtHU0FRTUl1Z1R3QVFBJTNEmgIiUEx3XzhJN2o2X1FGb2djTkZBLVpnd25EejdYOHJ2blZVTg%3D%3D"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US` (token = first page's trailing `continuationItemRenderer...token`)
- Parser: `get_continuations_2025` -> `parse_playlist_items(onResponseReceivedActions[0].appendContinuationItemsAction.continuationItems)`
- Assertions:
  1. continuationItems has 51 entries -> 51 tracks; no trailing continuationItemRenderer (last page). Total tracks 151 = trackCount.
  2. tracks[0] of this page (playlist index 100): title `"End of Line"`, videoId `-2C_P2N0LA8`, duration `2:37`.
  3. Last track (index 150) is greyed out (`musicItemRendererDisplayPolicy` = GREY_OUT): isAvailable false, videoId null, title `"Daft Punk - Alive 1997 (Twitch Live Stream 2-22-2022)"`, artists [{"name": "Branmo's Archive", "id": null}], duration `1:27:54` (5274 s — h:mm:ss form), likeStatus null.
  4. videoType histogram over all 151: {"MUSIC_VIDEO_TYPE_UGC": 11, "MUSIC_VIDEO_TYPE_OMV": 63, "MUSIC_VIDEO_TYPE_SHOULDER": 2, "MUSIC_VIDEO_TYPE_ATV": 74, "null": 1}; tracks with album: 74.
  5. `get_playlist(...)` duration_seconds over all tracks = 56970.

## watch_playlist.json

- Request: `POST next` body `{"enablePersistentPlaylistPanel": true, "isAudioOnly": true, "tunerSettingValue": "AUTOMIX_SETTING_NORMAL", "videoId": "hpSrLjc5SMs", "watchEndpointMusicSupportedConfigs": {"watchEndpointMusicConfig": {"hasPersistentPlaylistPanel": true, "musicVideoType": "MUSIC_VIDEO_TYPE_ATV"}}, "playlistId": "RDAMVMhpSrLjc5SMs"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_watch_playlist` -> `parse_watch_playlist(playlistPanelRenderer.contents)` + `get_tab_browse_ids`.
- Assertions:
  1. 50 tracks, playlistId `RDAMVMhpSrLjc5SMs`, lyrics `MPLYt_PITqkpE6ExP-3`, related `MPTRt_PITqkpE6ExP-3`.
  2. tracks[0]: videoId `hpSrLjc5SMs`, title `Wonderwall`, length `4:19`, artists [{"name": "Oasis", "id": "UCmMUZbaYdNH0bEd1PAlAqsA"}], album {"name": "(What's The Story) Morning Glory?", "id": "MPREb_PITqkpE6ExP"}, year `1995`, videoType `MUSIC_VIDEO_TYPE_ATV`.
  3. tracks[0].likeStatus null (anonymous: like toggle has no `likeEndpoint`), inLibrary false; no track has `counterpart`.
  4. tracks[1]: `Don't Look Back in Anger` X59TlszGtfM; tracks[-1]: `Drive` vtuZmShWLGo.
  5. `playlistPanelRenderer.continuations[0]` key is `nextRadioContinuationData` (ytmusicapi ctoken_path `Radio` because playlistId starts with `RDAMVM`).

## watch_radio.json

- Request: `POST next` body `{"enablePersistentPlaylistPanel": true, "isAudioOnly": true, "tunerSettingValue": "AUTOMIX_SETTING_NORMAL", "videoId": "hpSrLjc5SMs", "playlistId": "RDAMVMhpSrLjc5SMs", "params": "wAEB"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`
- Parser: `get_watch_playlist` -> `parse_watch_playlist(playlistPanelRenderer.contents)` + `get_tab_browse_ids`.
- Assertions:
  1. 50 tracks, playlistId `RDAMVMhpSrLjc5SMs`, lyrics `MPLYt_PITqkpE6ExP-3`, related `MPTRt_PITqkpE6ExP-3`.
  2. tracks[0]: videoId `hpSrLjc5SMs`, title `Wonderwall`, length `4:19`, artists [{"name": "Oasis", "id": "UCmMUZbaYdNH0bEd1PAlAqsA"}], album {"name": "(What's The Story) Morning Glory?", "id": "MPREb_PITqkpE6ExP"}, year `1995`, videoType `MUSIC_VIDEO_TYPE_ATV`.
  3. tracks[0].likeStatus null (anonymous: like toggle has no `likeEndpoint`), inLibrary false; no track has `counterpart`.
  4. tracks[1]: `Don't Look Back in Anger` X59TlszGtfM; tracks[-1]: `Fly Away` 2KeFjLDkOrI.
  5. `playlistPanelRenderer.continuations[0]` key is `nextRadioContinuationData` (ytmusicapi ctoken_path `Radio` because playlistId starts with `RDAMVM`).
  6. Compared with watch_playlist.json the queue diverges first at index 11 (radio params `wAEB`).

## lyrics.json

- Request: `POST browse` body `{"browseId": "MPLYt_PITqkpE6ExP-3"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US` (browseId from watch_playlist.json tab `MUSIC_PAGE_TYPE_TRACK_LYRICS`)
- Parser: `get_lyrics(browseId)` -> `contents.sectionListRenderer.contents[0].musicDescriptionShelfRenderer.description.runs[0].text`
- Assertions:
  1. hasTimestamps false; lyrics starts with "Today is gonna be the day\r\nThat they're gonn" (CRLF line breaks), length 1432 chars, 45 lines.
  2. lyrics ends with "onna be the one that saves me (saves me)".
  3. source is null in ytmusicapi (it reads `musicDescriptionShelfRenderer.runs[0].text`, which does not exist); the raw file has `musicDescriptionShelfRenderer.footer.runs[0].text` = `Source: LyricFind`.

## lyrics_timed.json

- Request: `POST browse` body `{"browseId": "MPLYt_PITqkpE6ExP-3"}` — client `ANDROID_MUSIC` `7.21.50` hl=`en` gl=`US` (ytmusicapi `as_mobile()` context)
- Parser: `get_lyrics(browseId, timestamps=True)` -> `contents.elementRenderer.newElement.type.componentType.model.timedLyricsModel.lyricsData`
- Assertions:
  1. hasTimestamps true, source `Source: LyricFind`, 40 lines.
  2. lines[0]: text "♪", start_time 0, end_time 22150, id 0 (ms values are JSON strings in the raw file).
  3. lines[1]: text "Today is gonna be the day", start_time 22150, end_time 24420, id 1.
  4. lines[-1]: text "You're gonna be the one that saves me (saves me)", start_time 217380, end_time 222950, id 39.
  5. Every `timedLyricsData[]` entry has `cueRange`; if any lacks it ytmusicapi falls back to plain `Lyrics` (joined `lyricLine`s, hasTimestamps false).

## lyrics_timed_unsynced.json

- Request: `POST browse` body `{"browseId": "MPLYt_OZf7JU9TYTI-4"}` — client `ANDROID_MUSIC` `7.21.50` hl=`en` gl=`US` (Maria Tanase, "Până Când Nu Te Iubeam", `ohIhBL3-deI`; lyrics browseId from its `next` tabs). Captured 2026-10-07.
- Parser: `get_lyrics(browseId, timestamps=True)`.
- Assertions:
  1. The mobile client answers **every** lyrics browse with `timedLyricsModel`; for lyrics that are not synced the 16 `timedLyricsData[]` entries have only `lyricLine` (no `cueRange`) and the model has `staticLayout: true`.
  2. ytmusicapi therefore returns plain `Lyrics`: hasTimestamps false, lyrics = the 16 `lyricLine`s joined with `\n` ("Pana cand nu te iubeam,\nDorule, dorule,\n...\nDorule, dorule!"), source `Source: Musixmatch`.
  3. Probed the same day: every other song with a lyrics tab (Oasis, Taylor Swift, Romanian folk) came back synced.

## related.json

- Request: `POST browse` body `{"browseId": "MPTRt_PITqkpE6ExP-3"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US` (browseId from watch_playlist.json tab `MUSIC_PAGE_TYPE_TRACK_RELATED`). Captured 2026-10-07.
- Parser: `get_song_related` -> `parse_mixed_content(contents.sectionListRenderer.contents)`.
- Assertions:
  1. 6 sections: ["You might also like", "Recommended playlists", "Other performances", "Similar artists", "Oasis", "About the artist"] with item counts [20, 10, 7, 9, 33] and a description string for the last.
  2. "You might also like" and "Other performances" are `musicResponsiveListItemRenderer` rows (`parse_song_flat`, carousel `numItemsPerColumn` 4); the rest are `musicTwoRowItemRenderer` cards.
  3. "You might also like"[0]: title `Wonderwall`, videoId `rj5wZqReXQE`, videoType `MUSIC_VIDEO_TYPE_ATV`, artists [{"name": "Oasis", "id": "UCmMUZbaYdNH0bEd1PAlAqsA"}], album {"name": "(What's The Story) Morning Glory? (Remastered)", "id": "MPREb_9nqEki4ZDpp"}, isExplicit false; [-1] title `Fix You`.
  4. "Recommended playlists"[0]: title `Modern Rock Hits`, playlistId `RDCLAK5uy_l3PeyHeqJh1dR78WjfsMJwRHJx9ofMvvc`, description `Playlist • YouTube Music`.
  5. "Other performances"[0]: title `Wonderwall`, videoId `bjoIKgVGiNQ`, artists [{"name": "TEEMID", "id": "UCB_tQU5gfHUCSFJ78-N538w"}], album {"name": "Wonderwall", "id": "MPREb_cNli1wG8jyp"}.
  6. "Similar artists"[0]: title `Liam Gallagher`, browseId `UCwK2Grm574W1u-sBzLikldQ`, subscribers `543K`.
  7. "Oasis"[0]: album `(What's The Story) Morning Glory? (30th Anniversary Deluxe Edition)`, browseId `MPREb_9rUb85NwrKk`, audioPlaylistId `OLAK5uy_mbNDS0pZVud699ZqH8T_-O9E5m7_XUSFE`, type `Album`, year `2025`, artists []; [1] `Whatever`, type `EP`, year `2025`.
  8. "About the artist" = `musicDescriptionShelfRenderer`: contents is the description string, starting "Oasis were a rock band consisting of Liam Gallagher, Paul “Guigsy” Mcguigan".

## player.json, player_error.json, player_login_required.json

- Request: `POST player` body `{"playbackContext": {"contentPlaybackContext": {"signatureTimestamp": 20732}}, "video_id": "<videoId>"}` — client `WEB_REMIX` `1.20261007.01.00` hl=`en` gl=`US`, **anonymous**. Captured 2026-10-07.
- Saved trimmed: only `playabilityStatus`, `playerConfig` and `videoDetails` are kept (`streamingData` carries per-client stream URLs), and the opaque per-session `playerConfig.mediaCommonConfig` blob is removed. ytmusicapi's `get_song` keeps neither `playerConfig` nor loudness; the loudness path is ours.
- Assertions:
  1. player.json: playabilityStatus `OK`; `playerConfig.audioConfig` = {loudnessDb -1.0799999, perceptualLoudnessDb -8.08, trackAbsoluteLoudnessLkfs -8.08, loudnessTargetLkfs -7, ...}. In all 6 playable tracks probed, loudnessDb == perceptualLoudnessDb - loudnessTargetLkfs.
  2. player_error.json: playabilityStatus `ERROR` "Video unavailable", no `playerConfig`.
  3. player_login_required.json: playabilityStatus `LOGIN_REQUIRED` "Sign in to confirm your age", no `playerConfig` (anonymous requests get no loudness for age-restricted tracks).

## Not captured (auth required)

Library (`FEmusic_liked_playlists`, `FEmusic_liked_videos`, `FEmusic_liked_albums`, `FEmusic_library_corpus_track_artists`, `FEmusic_library_corpus_artists`), liked songs (`VLLM`) and history (`FEmusic_history`) need a signed-in session; their shapes are documented from ytmusicapi's parser code in `docs/parsers-spec.md`. Capture them later with the app's own authenticated client.
