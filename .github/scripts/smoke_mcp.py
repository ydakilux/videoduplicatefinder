#!/usr/bin/env python3
"""Smoke-test the MCP stdio protocol of a published vdf-cli executable."""

import argparse
import json
import os
import selectors
import subprocess
import uuid
from pathlib import Path


PROTOCOL_VERSION = "2025-11-25"

TOOLS = {
    "get_scan_status",
    "get_active_filter",
    "set_active_filter",
    "get_duplicate_groups",
    "get_group_details",
    "get_group_thumbnails",
    "scroll_to_item",
    "highlight_group",
    "set_item_checked",
    "select_by_criteria",
    "audit_selection",
    "undo_selection",
    "clear_selection",
    "invert_selection",
    "mark_not_a_match",
    "remove_from_list",
    "rename_item",
    "transfer_suffix_and_rename",
}


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def rpc(method, **fields):
    return json.dumps({"jsonrpc": "2.0", "method": method, **fields})


def reject_non_json_constant(value):
    raise ValueError(f"Invalid JSON constant: {value}")


def smoke_test(executable):
    requests = [
        rpc("initialize", id=1, params={
            "protocolVersion": PROTOCOL_VERSION,
            "capabilities": {},
            "clientInfo": {"name": "vdf-ci-smoke", "version": "1"},
        }),
        rpc("notifications/initialized"),  # No response expected.
        '{"jsonrpc":"2.0",',
        '{"jsonrpc":"2.0","id":"bad-request"}',
        rpc("ping", id={}),
        rpc("ping", id="after-errors"),
        rpc("tools/list", id=2),
        rpc("tools/call", id=3, params={"name": "get_scan_status", "arguments": {}}),
    ]
    # Expected responses in wire order: label, ID (including null), error code.
    expected = [
        ("initialize", 1, None),
        ("malformed JSON", None, -32700),
        ("missing method", "bad-request", -32600),
        ("invalid ID", None, -32600),
        ("ping", "after-errors", None),
        ("tools/list", 2, None),
        ("tools/call", 3, None),
    ]
    # A unique pipe keeps the call offline even if a GUI is running locally.
    process = subprocess.Popen(
        [str(executable), "mcp", "--pipe-name", f"vdf-mcp-smoke-{uuid.uuid4().hex}"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        bufsize=0,
    )
    assert process.stdin is not None and process.stdout is not None and process.stderr is not None
    selector = selectors.DefaultSelector()
    selector.register(process.stdout, selectors.EVENT_READ)
    buffered = bytearray()

    def read_response_line():
        while b"\n" not in buffered:
            if not selector.select(30):
                raise AssertionError("MCP server did not emit the next response within 30 seconds")
            chunk = os.read(process.stdout.fileno(), 64 * 1024)
            if not chunk:
                raise AssertionError(f"MCP server closed stdout early: {bytes(buffered)!r}")
            buffered.extend(chunk)
        line, _, remainder = buffered.partition(b"\n")
        buffered[:] = remainder
        return line.decode("utf-8")

    try:
        process.stdin.write(("\n".join(requests) + "\n").encode("utf-8"))
        process.stdin.flush()
        lines = [read_response_line() for _ in expected]
        process.stdin.close()
        returncode = process.wait(timeout=30)
        extra_stdout = bytes(buffered) + process.stdout.read()
        stderr = process.stderr.read().decode("utf-8", errors="replace")
        require(returncode == 0, f"CLI exited with {returncode}; stderr: {stderr}")
        require(not extra_stdout, f"Unexpected extra stdout after expected responses: {extra_stdout!r}")
    finally:
        selector.close()
        if process.poll() is None:
            process.kill()
            process.wait()
        for stream in (process.stdin, process.stdout, process.stderr):
            if stream is not None:
                stream.close()

    results = {}
    for (label, expected_id, error_code), line in zip(expected, lines):
        try:
            response = json.loads(line, parse_constant=reject_non_json_constant)
        except ValueError as exc:
            raise AssertionError(f"{label}: expected one JSON response on this line, got {line!r}") from exc
        require(isinstance(response, dict) and response.get("jsonrpc") == "2.0",
                f"{label}: invalid JSON-RPC envelope: {response!r}")
        require("id" in response and type(response["id"]) is type(expected_id) and response["id"] == expected_id,
                f"{label}: expected ID {expected_id!r}, got {response!r}")
        if error_code is None:
            require("error" not in response and isinstance(response.get("result"), dict),
                    f"{label}: expected a result object, got {response!r}")
            results[label] = response["result"]
        else:
            error = response.get("error")
            require("result" not in response and isinstance(error, dict)
                    and type(error.get("code")) is int and error["code"] == error_code
                    and isinstance(error.get("message"), str) and error["message"],
                    f"{label}: expected JSON-RPC error {error_code}, got {response!r}")

    initialize = results["initialize"]
    require(initialize["protocolVersion"] == PROTOCOL_VERSION and initialize["serverInfo"]["name"] == "vdf-mcp",
            f"Unexpected initialize result: {initialize!r}")
    instructions = initialize.get("instructions")
    require(isinstance(instructions, str) and instructions.strip(),
            f"Initialize result is missing workflow instructions: {initialize!r}")
    require(results["ping"] == {}, f"Unexpected ping result: {results['ping']!r}")

    tools = results["tools/list"]["tools"]
    names = [tool["name"] for tool in tools]
    require(len(names) == len(TOOLS) and set(names) == TOOLS,
            f"Expected all {len(TOOLS)} tools exactly once, got {names!r}")

    offline = results["tools/call"]
    require(offline.get("isError") is True and any(
        item.get("type") == "text" and isinstance(item.get("text"), str)
        and "Cannot connect to VDF GUI pipe" in item["text"]
        for item in offline.get("content", [])
    ), f"Expected offline GUI connection error, got {offline!r}")
    print(f"MCP stdio smoke passed ({len(expected)} responses, {len(TOOLS)} tools).")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("executable", type=Path, help="Path to published vdf-cli")
    smoke_test(parser.parse_args().executable.resolve())
