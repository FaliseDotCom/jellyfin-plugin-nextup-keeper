#!/usr/bin/env bash
# Helpers for driving a test Jellyfin over its API; see .docs/TESTING.md. Source this file.
# JF_TEST_DIR holds config/, cache/, media/ and stash/sample.mkv for the test container.
JF=${JF_URL:-http://localhost:8096}
T=${JF_TEST_DIR:-"$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/run"}
AUTH_BASE='MediaBrowser Client="nuk-test", Device="cli", DeviceId="nuk-test-1", Version="1.0"'
STATE="$T/state.env"
[ -f "$STATE" ] && . "$STATE"

api()
{
  local method=$1 path=$2 body=${3:-}
  local auth="$AUTH_BASE"
  [ -n "${TOKEN:-}" ] && auth="$AUTH_BASE, Token=\"$TOKEN\""
  if [ -n "$body" ]; then
    curl -s -X "$method" "$JF$path" -H "Authorization: $auth" -H 'Content-Type: application/json' -d "$body"
  else
    curl -s -X "$method" "$JF$path" -H "Authorization: $auth"
  fi
}

login()
{
  local name=$1 pass=$2
  local r
  r=$(TOKEN= api POST /Users/AuthenticateByName "{\"Username\":\"$name\",\"Pw\":\"$pass\"}")
  TOKEN=$(echo "$r" | python -c "import json,sys; print(json.load(sys.stdin)['AccessToken'])" | tr -d '\r')
  USERID=$(echo "$r" | python -c "import json,sys; print(json.load(sys.stdin)['User']['Id'])" | tr -d '\r')
}

# Prints "Series SxxEyy" per line for the user's Next Up.
nextup()
{
  api GET "/Shows/NextUp?userId=$USERID&limit=${1:-24}&startIndex=${2:-0}${3:-}" | python -c "
import json,sys
d=json.load(sys.stdin)
print('total', d.get('TotalRecordCount'))
for i in d['Items']:
    print(' ', i.get('SeriesName'), 'S%02dE%02d' % (i.get('ParentIndexNumber') or 0, i.get('IndexNumber') or 0))
"
}

# Id of an episode by series name, season and episode number.
epid()
{
  api GET "/Items?userId=$USERID&IncludeItemTypes=Episode&Recursive=true&Fields=Path" | python -c "
import json,sys
for i in json.load(sys.stdin)['Items']:
    if i.get('SeriesName')=='$1' and i.get('ParentIndexNumber')==$2 and i.get('IndexNumber')==$3:
        print(i['Id'])
" | tr -d '\r'
}

seriesid()
{
  api GET "/Items?userId=$USERID&IncludeItemTypes=Series&Recursive=true" | python -c "
import json,sys
for i in json.load(sys.stdin)['Items']:
    if i['Name']=='$1': print(i['Id'])
" | tr -d '\r'
}

played() { api POST "/UserPlayedItems/$1?userId=$USERID" > /dev/null; }
unplayed() { api DELETE "/UserPlayedItems/$1?userId=$USERID" > /dev/null; }

# Starts a scan and waits until the scan task is idle again.
scan()
{
  api POST /Library/Refresh > /dev/null
  sleep 2
  for _ in $(seq 1 600); do
    local s
    s=$(api GET /ScheduledTasks | python -c "
import json,sys
for t in json.load(sys.stdin):
    if t.get('Key')=='RefreshLibrary': print(t['State'])
" | tr -d '\r')
    [ "$s" = "Idle" ] && return 0
    sleep 1
  done
  echo "scan timeout"
}

addep()
{
  local s=$1 season=$2 e=$3
  mkdir -p "$T/media/tv/$s/Season 0$season"
  cp "$T/stash/sample.mkv" "$T/media/tv/$s/Season 0$season/$s - S0${season}E0$e.mkv"
}

delep()
{
  rm -f "$T/media/tv/$1/Season 0$2/$1 - S0${2}E0$3.mkv"
}
