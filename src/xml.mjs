/**
 * 极简 XML 解析器 —— 只覆盖 WebDAV multistatus 响应所需的子集。
 *
 * 为什么手写而不是引依赖：本项目承诺零运行时依赖（见 ARCHITECTURE.md 第 4 节）。
 *
 * 支持：元素、属性、自闭合标签、文本、CDATA、注释、处理指令、DOCTYPE 跳过、
 *       五个预定义实体 + 数字字符引用。
 * 不支持：DTD 内部子集、外部实体（WebDAV 用不到，且实体扩展是 XXE 风险面）。
 *
 * **命名空间前缀被剥离**，只保留 local name —— `D:href` 和 `d:href` 都变成 `href`。
 * 这对 WebDAV 是安全的：响应里不会出现同名的不同命名空间元素。
 */

const NAME_END = /[\s=>/]/;

export function parseXml(src) {
  if (typeof src !== 'string') throw new TypeError('parseXml expects a string');
  let i = 0;

  function fail(msg) {
    const line = src.slice(0, i).split('\n').length;
    throw new Error(`XML parse error at line ${line}: ${msg}`);
  }

  function skipWs() {
    while (i < src.length && /\s/.test(src[i])) i++;
  }

  function localName(qname) {
    const c = qname.indexOf(':');
    return c === -1 ? qname : qname.slice(c + 1);
  }

  function decodeEntities(s) {
    if (s.indexOf('&') === -1) return s;
    return s.replace(/&(#[Xx]?[0-9A-Fa-f]+|[A-Za-z][A-Za-z0-9]*);/g, (m, body) => {
      if (body[0] === '#') {
        const hex = body[1] === 'x' || body[1] === 'X';
        const code = Number.parseInt(body.slice(hex ? 2 : 1), hex ? 16 : 10);
        if (!Number.isFinite(code) || code < 0 || code > 0x10ffff) return m;
        try {
          return String.fromCodePoint(code);
        } catch {
          return m;
        }
      }
      switch (body) {
        case 'amp': return '&';
        case 'lt': return '<';
        case 'gt': return '>';
        case 'quot': return '"';
        case 'apos': return "'";
        default: return m;
      }
    });
  }

  function readName() {
    const start = i;
    while (i < src.length && !NAME_END.test(src[i])) i++;
    if (i === start) fail('expected a name');
    return localName(src.slice(start, i));
  }

  function readAttrs() {
    const attrs = Object.create(null);
    for (;;) {
      skipWs();
      if (i >= src.length) fail('unexpected end of input inside a tag');
      if (src[i] === '>' || src[i] === '/') return attrs;
      const name = readName();
      skipWs();
      let value = '';
      if (src[i] === '=') {
        i++;
        skipWs();
        const quote = src[i];
        if (quote !== '"' && quote !== "'") fail('attribute value must be quoted');
        i++;
        const vs = i;
        while (i < src.length && src[i] !== quote) i++;
        if (i >= src.length) fail('unterminated attribute value');
        value = decodeEntities(src.slice(vs, i));
        i++;
      }
      attrs[name] = value;
    }
  }

  function skipSpecial() {
    // CDATA
    if (src.startsWith('<![CDATA[', i)) {
      const end = src.indexOf(']]>', i + 9);
      if (end === -1) fail('unterminated CDATA section');
      const text = src.slice(i + 9, end);
      i = end + 3;
      return { text };
    }
    // 注释
    if (src.startsWith('<!--', i)) {
      const end = src.indexOf('-->', i + 4);
      if (end === -1) fail('unterminated comment');
      i = end + 3;
      return { skipped: true };
    }
    // DOCTYPE —— 整段跳过，不做实体扩展
    if (src.startsWith('<!DOCTYPE', i)) {
      let depth = 0;
      while (i < src.length) {
        const ch = src[i];
        if (ch === '[') depth++;
        else if (ch === ']') depth--;
        else if (ch === '>' && depth <= 0) { i++; return { skipped: true }; }
        i++;
      }
      fail('unterminated DOCTYPE');
    }
    // 处理指令 / 其他 <! 结构
    if (src.startsWith('<?', i)) {
      const end = src.indexOf('?>', i + 2);
      if (end === -1) fail('unterminated processing instruction');
      i = end + 2;
      return { skipped: true };
    }
    if (src.startsWith('<!', i)) {
      const end = src.indexOf('>', i + 2);
      if (end === -1) fail('unterminated declaration');
      i = end + 1;
      return { skipped: true };
    }
    return null;
  }

  function parseNode() {
    if (src[i] !== '<') fail('expected "<"');
    i++;

    const name = readName();
    const attrs = readAttrs();

    if (src[i] === '/') {
      i++;
      if (src[i] !== '>') fail('expected ">" after "/"');
      i++;
      return { name, attrs, children: [], text: '' };
    }
    if (src[i] !== '>') fail('expected ">" to close a tag');
    i++;

    const node = { name, attrs, children: [], text: '' };
    for (;;) {
      if (i >= src.length) fail(`unterminated element <${name}>`);

      if (src[i] === '<') {
        if (src[i + 1] === '/') {
          i += 2;
          const closing = readName();
          skipWs();
          if (src[i] !== '>') fail('expected ">" in closing tag');
          i++;
          if (closing !== name) fail(`mismatched closing tag </${closing}>, expected </${name}>`);
          return node;
        }
        const special = skipSpecial();
        if (special) {
          if (special.text) node.text += special.text;
          continue;
        }
        node.children.push(parseNode());
        continue;
      }

      const start = i;
      while (i < src.length && src[i] !== '<') i++;
      node.text += decodeEntities(src.slice(start, i));
    }
  }

  const root = { name: '#document', attrs: Object.create(null), children: [], text: '' };
  for (;;) {
    skipWs();
    if (i >= src.length) break;
    if (src[i] !== '<') fail('text content outside of the root element');
    const special = skipSpecial();
    if (special) continue;
    root.children.push(parseNode());
  }
  return root;
}

/** 直接子元素里第一个匹配 local name 的节点。 */
export function child(node, name) {
  if (!node?.children) return null;
  for (const c of node.children) if (c.name === name) return c;
  return null;
}

/** 直接子元素里所有匹配 local name 的节点。 */
export function children(node, name) {
  if (!node?.children) return [];
  return node.children.filter((c) => c.name === name);
}

/**
 * 递归查找第一个匹配 local name 的后代（深度优先）。
 * 用于跳过 WebDAV 响应里 `propstat > prop > X` 这种固定层级。
 */
export function find(node, name) {
  if (!node?.children) return null;
  for (const c of node.children) {
    if (c.name === name) return c;
    const hit = find(c, name);
    if (hit) return hit;
  }
  return null;
}

/** 递归查找所有匹配 local name 的后代。 */
export function findAll(node, name, out = []) {
  if (!node?.children) return out;
  for (const c of node.children) {
    if (c.name === name) out.push(c);
    findAll(c, name, out);
  }
  return out;
}

/** 节点的去空白文本内容（含 CDATA 片段）。 */
export function text(node) {
  return node ? node.text.trim() : '';
}
