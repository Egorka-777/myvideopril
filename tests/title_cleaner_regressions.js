"use strict";

const assert = require("assert");

function cleanLikeCSharp(title) {
  if (!title || !String(title).trim()) return "";
  let t = String(title).trim();
  t = t.replace(/\.(mp4|mov|mkv|webm|m4v|avi|mts|m2ts|ts|wmv)\s*$/i, "");
  const leading = /^\s*(?:\[\s*\d+\s*\]|\(\s*\d+\s*\)|\d+\s*[\._\-]\s*|\d+\.\s*|\d+\s+)/;
  while (true) {
    const m = t.match(leading);
    if (!m) break;
    t = t.slice(m[0].length).trim();
  }
  t = t.replace(/(?<=\S)[\._]+(?=\S)/g, " ");
  t = t.replace(/\s+/g, " ").trim();
  return t.replace(/^[\s\._\-]+|[\s\._\-]+$/g, "");
}

const sample = "001. Трейдинг на Бинодекс . Бинарные опционы стратегия . BinoDex.mp4";
const got = cleanLikeCSharp(sample);
assert.ok(got.includes("Трейдинг"), "keeps words");
assert.ok(got.includes("BinoDex"), "keeps brand");
assert.ok(!got.startsWith("001"), "removes leading index");
assert.ok(cleanLikeCSharp("12.34 стратегия 2024").includes("2024"), "keeps meaningful digits");
assert.strictEqual(cleanLikeCSharp("[01] Тест").slice(0, 4), "Тест");

console.log("title cleaner regression checks passed");
