import test from 'node:test';
import assert from 'node:assert/strict';

import { child, children, findAll, find, parseXml, text } from '../src/xml.mjs';

test('解析元素、属性、嵌套', () => {
  const doc = parseXml('<a x="1" y=\'2\'><b>hi</b><c/></a>');
  const a = doc.children[0];
  assert.equal(a.name, 'a');
  assert.equal(a.attrs.x, '1');
  assert.equal(a.attrs.y, '2');
  assert.equal(child(a, 'b').text, 'hi');
  assert.equal(child(a, 'c').children.length, 0);
});

test('剥离命名空间前缀', () => {
  const doc = parseXml('<D:multistatus xmlns:D="DAV:"><D:response><D:href>/x</D:href></D:response></D:multistatus>');
  assert.equal(doc.children[0].name, 'multistatus');
  assert.equal(find(doc, 'href').text, '/x');
});

test('实体与数字字符引用', () => {
  const doc = parseXml('<t>a &amp; b &lt;c&gt; &quot;d&quot; &apos;e&apos; &#65; &#x42;</t>');
  assert.equal(text(doc.children[0]), 'a & b <c> "d" \'e\' A B');
});

test('CDATA 与注释', () => {
  const doc = parseXml('<t><![CDATA[a < b & c]]><!-- note -->tail</t>');
  assert.equal(text(doc.children[0]), 'a < b & ctail');
});

test('处理指令与 DOCTYPE 被跳过', () => {
  const doc = parseXml('<?xml version="1.0"?><!DOCTYPE x [<!ENTITY e "v">]><root>ok</root>');
  assert.equal(doc.children.length, 1);
  assert.equal(text(doc.children[0]), 'ok');
});

test('自闭合与混合内容', () => {
  const doc = parseXml('<r>pre<b/>post</r>');
  assert.equal(text(doc.children[0]), 'prepost');
  assert.equal(doc.children[0].children.length, 1);
});

test('find / findAll 递归查找', () => {
  const doc = parseXml('<r><a><b><c>1</c></b></a><a><c>2</c></a></r>');
  assert.equal(text(find(doc, 'c')), '1');
  assert.equal(findAll(doc, 'c').length, 2);
  assert.equal(children(doc.children[0], 'a').length, 2);
  assert.equal(find(doc, 'missing'), null);
});

test('畸形输入抛出可读错误而不是静默返回', () => {
  assert.throws(() => parseXml('<a><b></a>'), /mismatched closing tag/);
  assert.throws(() => parseXml('<a'), /unterminated|unexpected end/);
  assert.throws(() => parseXml('<a x=1></a>'), /quoted/);
  assert.throws(() => parseXml('<a><!-- unterminated'), /unterminated comment/);
  assert.throws(() => parseXml('<a><![CDATA[oops</a>'), /unterminated CDATA/);
  assert.throws(() => parseXml('bare text'), /outside of the root/);
});

test('错误信息带行号', () => {
  try {
    parseXml('<a>\n<b>\n</c>\n</a>');
    assert.fail('应当抛出');
  } catch (err) {
    assert.match(err.message, /line 3/);
  }
});

test('不因未知实体崩溃', () => {
  const doc = parseXml('<t>&unknown; &amp;</t>');
  assert.equal(text(doc.children[0]), '&unknown; &');
});
