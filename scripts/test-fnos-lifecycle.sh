#!/usr/bin/env bash

set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: test-fnos-lifecycle.sh <staged-package-root>" >&2
  exit 2
fi

package_root="$(realpath "$1")"
runtime_root="$(mktemp -d /tmp/clipport-fnos-lifecycle-XXXXXX)"
installed_root="$runtime_root/package"
mkdir -p "$installed_root"
cp -a "$package_root"/. "$installed_root"

cleanup() {
  exit_code=$?
  TRIM_APPDEST="$installed_root/app" \
    TRIM_PKGVAR="$runtime_root/var" \
    TRIM_TEMP_LOGFILE="$runtime_root/lifecycle-error.log" \
    bash "$package_root/cmd/main" stop >/dev/null 2>&1 || true
  if [[ "$exit_code" -ne 0 ]]; then
    echo 'fnOS lifecycle simulation diagnostics:' >&2
    for diagnostic in "$runtime_root/lifecycle-error.log" "$runtime_root/var/clipport.log"; do
      if [[ -f "$diagnostic" ]]; then
        echo "--- $diagnostic" >&2
        sed -n '1,160p' "$diagnostic" >&2
      fi
    done
  fi
  case "$runtime_root" in
    /tmp/clipport-fnos-lifecycle-*) rm -rf -- "$runtime_root" ;;
    *) echo "Refusing unsafe lifecycle cleanup path: $runtime_root" >&2 ;;
  esac
}
trap cleanup EXIT

mkdir -p "$runtime_root/var"
export TRIM_APPDEST="$installed_root/app"
export TRIM_PKGVAR="$runtime_root/var"
export TRIM_TEMP_LOGFILE="$runtime_root/lifecycle-error.log"
export TRIM_API_TOKEN="test-lifecycle-token"
export TRIM_SYS_LANGUAGE="zh-CN"
export TRIM_SYS_VERSION="1.2.0401"

if [[ "${CLIPPORT_LIFECYCLE_TRACE:-0}" == "1" ]]; then
  bash -x "$installed_root/cmd/main" start
else
  bash "$installed_root/cmd/main" start
fi
bash "$installed_root/cmd/main" start
bash "$installed_root/cmd/main" status

socket_path="$installed_root/app/app.sock"
test -S "$socket_path"
session_json="$(curl --silent --show-error --unix-socket "$socket_path" \
  -H 'X-Trim-Userid: 1000' \
  -H 'X-Trim-Username: admin' \
  -H 'X-Trim-Isadmin: true' \
  http://localhost/app/clipport/api/v1/session)"
grep -q '"isAdmin":true' <<< "$session_json"
grep -q '"isCompatible":true' <<< "$session_json"
if grep -q "$TRIM_API_TOKEN" <<< "$session_json"; then
  echo 'TRIM_API_TOKEN leaked through the session response.' >&2
  exit 1
fi

csrf_token="$(sed -n 's/.*"csrfToken":"\([^"]*\)".*/\1/p' <<< "$session_json")"
test -n "$csrf_token"
webhook_canary='lifecycle-webhook-secret'
smtp_canary='lifecycle-smtp-secret'
settings_response="$(curl --silent --show-error --unix-socket "$socket_path" \
  -X PUT \
  -H 'Content-Type: application/json' \
  -H 'X-Trim-Userid: 1000' \
  -H 'X-Trim-Username: admin' \
  -H 'X-Trim-Isadmin: true' \
  -H "X-ClipPort-CSRF: $csrf_token" \
  --data "{\"theme\":\"system\",\"accent\":\"system\",\"language\":\"simplifiedChinese\",\"reportExportDirectory\":null,\"notifyOnTaskCompleted\":true,\"notifyOnTaskFailed\":true,\"channels\":[{\"id\":\"lifecycle\",\"displayName\":\"lifecycle\",\"kind\":\"feishu\",\"isEnabled\":false,\"endpoint\":\"https://example.invalid/$webhook_canary\",\"clearEndpoint\":false,\"smtpHost\":\"\",\"smtpPort\":465,\"smtpUsername\":\"\",\"smtpPassword\":\"$smtp_canary\",\"clearSmtpPassword\":false,\"smtpFrom\":\"\",\"smtpRecipients\":\"\"}]}" \
  http://localhost/app/clipport/api/v1/settings)"
if grep -Eq "$TRIM_API_TOKEN|$webhook_canary|$smtp_canary" <<< "$settings_response"; then
  echo 'A credential leaked through the settings response.' >&2
  exit 1
fi
grep -q '"hasEndpoint":true' <<< "$settings_response"
grep -q 'fnosdp:' "$runtime_root/var/settings.json"
test "$(stat -c '%a' "$runtime_root/var/settings.json")" = '600'
test "$(stat -c '%a' "$runtime_root/var/keys")" = '700'
if grep -Eq "$webhook_canary|$smtp_canary" "$runtime_root/var/settings.json"; then
  echo 'A notification credential was stored in plaintext.' >&2
  exit 1
fi

bash "$installed_root/cmd/main" stop
if bash "$installed_root/cmd/main" status; then
  echo 'Stopped ClipPort unexpectedly reported a running status.' >&2
  exit 1
else
  status_code=$?
  test "$status_code" -eq 3
fi
test ! -e "$socket_path"

if grep -Eq "$TRIM_API_TOKEN|$webhook_canary|$smtp_canary" "$runtime_root/var/clipport.log"; then
  echo 'A credential leaked into clipport.log.' >&2
  exit 1
fi

echo 'fnOS lifecycle simulation passed.'
