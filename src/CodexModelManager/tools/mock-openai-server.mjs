import http from "node:http";

const host = "127.0.0.1";
const port = 18888;
const expectedKey = "test-key-only";

function sendJson(response, status, body) {
  response.writeHead(status, { "content-type": "application/json; charset=utf-8" });
  response.end(JSON.stringify(body));
}

function authorized(request) {
  return request.headers.authorization === `Bearer ${expectedKey}`;
}

const server = http.createServer((request, response) => {
  const url = new URL(request.url ?? "/", `http://${host}:${port}`);
  process.stdout.write(`${request.method} ${url.pathname}\n`);

  if (!authorized(request)) {
    sendJson(response, 401, { error: { message: "invalid test key" } });
    return;
  }

  if (request.method === "GET" && url.pathname === "/v1/models") {
    sendJson(response, 200, {
      object: "list",
      data: [{ id: "k3-test", object: "model", owned_by: "local-test" }],
    });
    return;
  }

  if (request.method === "POST" && url.pathname === "/v1/chat/completions") {
    let raw = "";
    request.on("data", chunk => { raw += chunk; });
    request.on("end", () => {
      let body = {};
      try { body = JSON.parse(raw); } catch {}
      if (body.stream) {
        response.writeHead(200, {
          "content-type": "text/event-stream; charset=utf-8",
          "cache-control": "no-cache",
          connection: "keep-alive",
        });
        const created = Math.floor(Date.now() / 1000);
        response.write(`data: ${JSON.stringify({
          id: "chatcmpl-local-test",
          object: "chat.completion.chunk",
          created,
          model: "k3-test",
          choices: [{ index: 0, delta: { role: "assistant", content: "CUSTOM_MODEL_OK" }, finish_reason: null }],
        })}\n\n`);
        response.write(`data: ${JSON.stringify({
          id: "chatcmpl-local-test",
          object: "chat.completion.chunk",
          created,
          model: "k3-test",
          choices: [{ index: 0, delta: {}, finish_reason: "stop" }],
          usage: { prompt_tokens: 10, completion_tokens: 3, total_tokens: 13 },
        })}\n\n`);
        response.end("data: [DONE]\n\n");
      } else {
        sendJson(response, 200, {
          id: "chatcmpl-local-test",
          object: "chat.completion",
          created: Math.floor(Date.now() / 1000),
          model: "k3-test",
          choices: [{ index: 0, message: { role: "assistant", content: "CUSTOM_MODEL_OK" }, finish_reason: "stop" }],
          usage: { prompt_tokens: 10, completion_tokens: 3, total_tokens: 13 },
        });
      }
    });
    return;
  }

  sendJson(response, 404, { error: { message: "not found" } });
});

server.listen(port, host, () => {
  process.stdout.write(`READY http://${host}:${port}/v1\n`);
});

for (const signal of ["SIGINT", "SIGTERM"]) {
  process.on(signal, () => server.close(() => process.exit(0)));
}
