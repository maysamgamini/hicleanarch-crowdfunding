// Bearer tokens for a run. In an AWS account they come from TOKENS_FILE (scripts/load.sh signs users
// in to Cognito). Against a local host, LOCAL_SIGNING_KEY signs tokens shaped like the host's
// LocalTokens (src/BuildingBlocks/.../Identity/LocalTokens.cs): HS256, issuer hicleanarch-local, and the
// claims sub, client_id, token_use and cognito:groups. A local backer is new for every pledge.

import crypto from "k6/crypto";
import encoding from "k6/encoding";

const KEY = __ENV.LOCAL_SIGNING_KEY;
const CLIENT_ID = __ENV.LOCAL_CLIENT_ID || "local-client";
const file = __ENV.TOKENS_FILE ? JSON.parse(open(__ENV.TOKENS_FILE)) : null;

if (!KEY && !file) throw new Error("Set TOKENS_FILE (an AWS account) or LOCAL_SIGNING_KEY (local host).");

function uuid() {
  const b = new Uint8Array(crypto.randomBytes(16));
  b[6] = (b[6] & 0x0f) | 0x40;
  b[8] = (b[8] & 0x3f) | 0x80;
  const h = Array.from(b, (x) => x.toString(16).padStart(2, "0")).join("");
  return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}

function sign(group) {
  const now = Math.floor(Date.now() / 1000);
  const part = (o) => encoding.b64encode(JSON.stringify(o), "rawurl");
  const body = `${part({ alg: "HS256", typ: "JWT" })}.${part({
    iss: "hicleanarch-local", sub: uuid(), client_id: CLIENT_ID, token_use: "access",
    "cognito:groups": [group], iat: now, nbf: now - 5, exp: now + 3600,
  })}`;
  return `${body}.${crypto.hmac("sha256", KEY, body, "base64rawurl")}`;
}

export function creatorToken() {
  return file ? file.creator : sign("creator");
}

/** The n-th backer's token: from the file, round robin; or a new local backer each time. */
export function backerToken(n) {
  return file ? file.backers[n % file.backers.length] : sign("backer");
}
