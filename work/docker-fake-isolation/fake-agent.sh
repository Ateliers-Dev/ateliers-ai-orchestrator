#!/bin/sh
set -eu

role="${1:?role required}"
rootfs_mode="${2:-readonly}"
case "$rootfs_mode" in
    readonly|writable-control) ;;
    *) echo 'unknown_rootfs_mode' >&2; exit 24 ;;
esac
if [ "$role" = "reviewer" ]; then
    artifact_content="${3:?fixed artifact required}"
    expected_hash="${4:?artifact hash required}"
    actual_hash="$(printf '%s' "$artifact_content" | sha256sum | awk '{ print $1 }')"
    printf 'review_artifact_sha256=%s\n' "$actual_hash"
    if [ "$actual_hash" != "$expected_hash" ]; then
        echo 'review_artifact_hash=invalid' >&2
        exit 26
    fi
fi
printf 'role=%s\n' "$role"
printf 'uid=%s gid=%s\n' "$(id -u)" "$(id -g)"

printf 'capabilities='
awk '/^CapEff:/ { print $2 }' /proc/self/status
printf 'no_new_privs='
awk '/^NoNewPrivs:/ { print $2 }' /proc/self/status

printf 'memory_max='
cat /sys/fs/cgroup/memory.max
printf 'cpu_max='
cat /sys/fs/cgroup/cpu.max
printf 'pids_max='
cat /sys/fs/cgroup/pids.max

if touch /read-only-probe/write-probe 2>/dev/null; then
    if [ "$rootfs_mode" = "readonly" ]; then
        echo 'rootfs_write=unexpectedly_allowed'
        exit 20
    fi
    echo 'rootfs_write=allowed_writable_control'
else
    if [ "$rootfs_mode" = "writable-control" ]; then
        echo 'rootfs_write=unexpectedly_denied_control'
        exit 25
    fi
    echo 'rootfs_write=denied_readonly'
fi

if [ "$role" = "implementer" ]; then
    mkdir -p /tmp/role-a
    printf 'synthetic-canary\n' > /tmp/role-a/canary
    echo 'role_a_tmpfs_write=allowed'
elif [ "$role" = "reviewer" ]; then
    if [ -e /tmp/role-a/canary ]; then
        echo 'role_a_canary_visible=unexpectedly_visible'
        exit 21
    else
        echo 'role_a_canary_visible=no'
    fi
    mkdir -p /tmp/role-b
    printf 'synthetic-canary\n' > /tmp/role-b/canary
    echo 'role_b_tmpfs_write=allowed'
    echo 'review_artifact_hash=verified'
else
    echo 'unknown_role'
    exit 22
fi

route_count="$(awk 'NR > 1 && $1 != "lo" { count++ } END { print count+0 }' /proc/net/route)"
printf 'non_loopback_ipv4_route_count=%s\n' "$route_count"
if [ "$route_count" -ne 0 ]; then
    exit 23
fi

echo 'fake_result=pass'
