// The Markdown extension recognizes dollar runs regardless of their surroundings.
// Keep prices literal and make a standalone pair of double dollars display math.
export function refineReplyMath() {
  return (tree, file) => {
    const source = String(file.value);
    function visit(parent) {
      for (let index = 0; index < parent.children?.length; index++) {
        const node = parent.children[index];
        if (node.type !== "inlineMath") {
          visit(node);
          continue;
        }
        const { start, end } = node.position;
        const text = source.slice(start.offset, end.offset);
        if (/^\$(?!\$)/.test(text) && (/\s/.test(text[1]) || /\s/.test(text.at(-2)) ||
            /^\d/.test(source.slice(end.offset, end.offset + 1)))) {
          parent.children[index] = { type: "text", value: text, position: node.position };
        } else if (parent.type === "paragraph" && parent.children.length === 1 && /^\$\$/.test(text)) {
          node.data.hProperties.className = ["language-math", "math-display"];
        }
      }
    }
    visit(tree);
  };
}
