import { Fragment, type ReactNode } from "react";

function inline(text: string): ReactNode[] {
  const tokens = text.split(/(\*\*[^*\n]+\*\*|__[^_\n]+__|\*[^*\n]+\*|_[^_\n]+_)/g);
  return tokens.map((token, index) => {
    if (/^(\*\*|__)/.test(token)) return <strong key={index}>{token.slice(2, -2)}</strong>;
    if (/^(\*|_)/.test(token) && token.length > 2) return <em key={index}>{token.slice(1, -1)}</em>;
    return <Fragment key={index}>{token}</Fragment>;
  });
}

export function SafeMarkdown({ text }: { text: string }) {
  const blocks: ReactNode[] = [];
  const lines = text.replaceAll("\r\n", "\n").split("\n");
  let index = 0;
  while (index < lines.length) {
    if (!lines[index].trim()) {
      index++;
      continue;
    }
    const list = lines[index].match(/^\s*(?:([-*+])|\d+[.)])\s+(.+)$/);
    if (list) {
      const ordered = !list[1];
      const items: ReactNode[] = [];
      while (index < lines.length) {
        const item = lines[index].match(/^\s*(?:([-*+])|\d+[.)])\s+(.+)$/);
        if (!item || Boolean(item[1]) === ordered) break;
        items.push(<li key={index}>{inline(item[2])}</li>);
        index++;
      }
      blocks.push(ordered ? <ol key={index}>{items}</ol> : <ul key={index}>{items}</ul>);
    } else {
      const paragraph: string[] = [];
      do {
        paragraph.push(lines[index++]);
      } while (index < lines.length && lines[index].trim() && !/^\s*(?:[-*+]|\d+[.)])\s+/.test(lines[index]));
      blocks.push(<p key={index}>{inline(paragraph.join(" "))}</p>);
    }
  }
  return <div className="message-markdown">{blocks}</div>;
}
