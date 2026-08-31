#!/usr/bin/env python3
"""Drive the self-hosted Penpot MCP server over streamable HTTP.

WHY THIS EXISTS RATHER THAN THE MCP CLIENT: registering the server in
``~/.claude.json`` only takes effect at the *next* session start, so an agent
that just added it cannot use it. This talks the same protocol directly, which
means a design can be built in the session that wired the server up.

THE PRECONDITION IS A BROWSER, AND NOTHING REPORTS ITS ABSENCE CLEARLY.
``execute_code`` runs inside the Penpot *plugin* context, not on the server, so
a Penpot file must be open in a browser with the MCP plugin connected. With no
bridge the call fails on a timeout rather than on a message that names the
cause -- check ``penpot.currentFile.name`` first, which is one round trip.

The token is a Penpot *user* token: it authenticates as the person who minted
it, so it never goes in the repo. Supply it as ``PENPOT_MCP_TOKEN``, or put the
full URL in ``PENPOT_MCP_URL``. Mint one from the Penpot MCP plugin panel.

    export PENPOT_MCP_TOKEN=...
    python tooling/scripts/penpot_mcp.py check
    python tooling/scripts/penpot_mcp.py js build.js
    python tooling/scripts/penpot_mcp.py png <shape-id> out.png

The stack itself is `docker compose -p penpot` out of ``C:\\Users\\User\\penpot``;
the frontend proxies ``/mcp/stream`` to the ``penpot-mcp`` container on 4401.
"""
from __future__ import annotations

import base64
import json
import os
import re
import sys
import urllib.request

DEFAULT_BASE = "http://localhost:9001/mcp/stream"


def _url() -> str:
    explicit = os.environ.get("PENPOT_MCP_URL")
    if explicit:
        return explicit
    token = os.environ.get("PENPOT_MCP_TOKEN")
    if not token:
        sys.exit("set PENPOT_MCP_TOKEN (or PENPOT_MCP_URL); mint one in the "
                 "Penpot MCP plugin panel")
    base = os.environ.get("PENPOT_MCP_BASE", DEFAULT_BASE)
    return f"{base}?userToken={token}"


class Penpot:
    def __init__(self, url: str | None = None, timeout: int = 180) -> None:
        self.url = url or _url()
        self.timeout = timeout
        self.session: str | None = None
        self._id = 0
        self._connect()

    def _post(self, body: dict) -> str:
        headers = {"Content-Type": "application/json",
                   "Accept": "application/json, text/event-stream"}
        if self.session:
            headers["mcp-session-id"] = self.session
        req = urllib.request.Request(self.url, json.dumps(body).encode(), headers)
        resp = urllib.request.urlopen(req, timeout=self.timeout)
        if not self.session:
            self.session = resp.headers.get("mcp-session-id")
        return resp.read().decode()

    def _rpc(self, method: str, params: dict | None = None) -> dict:
        self._id += 1
        raw = self._post({"jsonrpc": "2.0", "id": self._id,
                          "method": method, "params": params or {}})
        # streamable HTTP answers as SSE; the last data: frame is the result
        frames = re.findall(r"^data: (.*)$", raw, re.M)
        return json.loads(frames[-1] if frames else raw)

    def _connect(self) -> None:
        self._rpc("initialize", {
            "protocolVersion": "2025-06-18", "capabilities": {},
            "clientInfo": {"name": "emberfall-tooling", "version": "1"}})
        self._post({"jsonrpc": "2.0", "method": "notifications/initialized"})

    def call(self, name: str, args: dict | None = None) -> dict:
        r = self._rpc("tools/call", {"name": name, "arguments": args or {}})
        if "error" in r:
            raise RuntimeError(json.dumps(r["error"]))
        return r.get("result", {})

    @staticmethod
    def _text(result: dict) -> str:
        return "\n".join(c.get("text", "") for c in result.get("content", [])
                         if c.get("type") == "text")

    def js(self, code: str) -> str:
        """Run JavaScript in the plugin context. Return a value; do not log it."""
        result = self.call("execute_code", {"code": code})
        out = self._text(result)
        return ("TOOL ERROR: " + out) if result.get("isError") else out

    def png(self, shape_id: str, path: str) -> bool:
        result = self.call("export_shape", {"shapeId": shape_id, "format": "png"})
        for chunk in result.get("content", []):
            if chunk.get("type") == "image":
                with open(path, "wb") as fh:
                    fh.write(base64.b64decode(chunk["data"]))
                return True
        return False


def main(argv: list[str]) -> int:
    if not argv:
        print(__doc__)
        return 0
    pp = Penpot()
    cmd = argv[0]
    if cmd == "check":
        print(pp.js("return { file: penpot.currentFile?.name,"
                    " page: penpot.currentPage?.name };"))
    elif cmd == "js":
        target = argv[1]
        code = (open(target, encoding="utf-8").read()
                if os.path.exists(target) else target)
        print(pp.js(code))
    elif cmd == "png":
        ok = pp.png(argv[1], argv[2])
        print("saved " + argv[2] if ok else "no image returned")
        return 0 if ok else 1
    elif cmd == "tools":
        for t in pp._rpc("tools/list")["result"]["tools"]:
            print(f"{t['name']}: {(t.get('description') or '').splitlines()[0]}")
    else:
        print(pp._text(pp.call(cmd, json.loads(argv[1]) if len(argv) > 1 else {})))
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
