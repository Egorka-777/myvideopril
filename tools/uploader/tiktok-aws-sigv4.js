"use strict";

const crypto = require("crypto");

const EMPTY_PAYLOAD_HASH = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

function sha256Hex(data) {
  return crypto.createHash("sha256").update(data == null ? "" : data, "utf8").digest("hex");
}

function hmacSha256(key, data, encoding) {
  return crypto.createHmac("sha256", key).update(data, encoding).digest();
}

/** RFC3986 encode (AWS SigV4). */
function rfc3986Encode(str) {
  return encodeURIComponent(String(str))
    .replace(/[!'()*]/g, ch => "%" + ch.charCodeAt(0).toString(16).toUpperCase());
}

function normalizeCanonicalUri(pathname) {
  if (!pathname || pathname === "") return "/";
  const parts = pathname.split("/");
  return parts.map((seg, i) => (i === 0 ? "" : rfc3986Encode(seg))).join("/") || "/";
}

function buildCanonicalQueryString(url) {
  const parsed = new URL(url);
  const pairs = [];
  parsed.searchParams.forEach((value, key) => pairs.push([key, value]));
  pairs.sort((a, b) => {
    const ak = rfc3986Encode(a[0]);
    const bk = rfc3986Encode(b[0]);
    if (ak !== bk) return ak < bk ? -1 : 1;
    const av = rfc3986Encode(a[1]);
    const bv = rfc3986Encode(b[1]);
    if (av !== bv) return av < bv ? -1 : 1;
    return 0;
  });
  return pairs.map(([k, v]) => `${rfc3986Encode(k)}=${rfc3986Encode(v)}`).join("&");
}

/**
 * AWS SigV4 signing. Returns headers plus debug material for tests.
 * CanonicalURI excludes query; CanonicalQueryString is sorted/encoded separately.
 */
function awsSigV4Sign({
  method,
  url,
  body = "",
  accessKeyId,
  secretAccessKey,
  sessionToken,
  region,
  service,
  amzDate
}) {
  const parsed = new URL(url);
  const datetime = amzDate || new Date().toISOString().replace(/[:-]|\.\d{3}/g, "");
  const dateStamp = datetime.slice(0, 8);
  const upperMethod = String(method || "GET").toUpperCase();
  const payloadHash = sha256Hex(body == null ? "" : body);
  const canonicalUri = normalizeCanonicalUri(parsed.pathname);
  const canonicalQuery = buildCanonicalQueryString(url);

  const canonicalHeadersObj = {
    host: parsed.host,
    "x-amz-content-sha256": payloadHash,
    "x-amz-date": datetime
  };
  if (sessionToken) canonicalHeadersObj["x-amz-security-token"] = sessionToken;

  const signedHeaderKeys = Object.keys(canonicalHeadersObj).map(k => k.toLowerCase()).sort();
  const canonicalHeaderString = signedHeaderKeys
    .map(k => `${k}:${String(canonicalHeadersObj[k]).trim()}\n`)
    .join("");
  const signedHeaders = signedHeaderKeys.join(";");

  const canonicalRequest = [
    upperMethod,
    canonicalUri,
    canonicalQuery,
    canonicalHeaderString,
    signedHeaders,
    payloadHash
  ].join("\n");

  const credentialScope = `${dateStamp}/${region}/${service}/aws4_request`;
  const stringToSign = [
    "AWS4-HMAC-SHA256",
    datetime,
    credentialScope,
    sha256Hex(canonicalRequest)
  ].join("\n");

  const kDate = hmacSha256("AWS4" + secretAccessKey, dateStamp);
  const kRegion = hmacSha256(kDate, region);
  const kService = hmacSha256(kRegion, service);
  const kSigning = hmacSha256(kService, "aws4_request");
  const signature = hmacSha256(kSigning, stringToSign).toString("hex");
  const authorization = `AWS4-HMAC-SHA256 Credential=${accessKeyId}/${credentialScope}, SignedHeaders=${signedHeaders}, Signature=${signature}`;

  const headers = Object.assign({}, canonicalHeadersObj, { Authorization: authorization });
  return {
    headers,
    canonicalRequest,
    stringToSign,
    signature,
    signedHeaders,
    payloadHash,
    canonicalUri,
    canonicalQuery,
    amzDate: datetime,
    credentialScope
  };
}

/** AWS documentation-style golden vector (empty-body GET). */
function awsGoldenVector() {
  const url = "https://example.amazonaws.com/?Action=ApplyUploadInner&FileSize=123&Version=2020-11-19";
  return awsSigV4Sign({
    method: "GET",
    url,
    body: "",
    accessKeyId: "AKIAIOSFODNN7EXAMPLE",
    secretAccessKey: "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
    sessionToken: "tok-example",
    region: "ap-singapore-1",
    service: "vod",
    amzDate: "20260924T120000Z"
  });
}

module.exports = {
  EMPTY_PAYLOAD_HASH,
  sha256Hex,
  rfc3986Encode,
  normalizeCanonicalUri,
  buildCanonicalQueryString,
  awsSigV4Sign,
  awsGoldenVector
};
