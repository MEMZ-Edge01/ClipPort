"use strict";
Object.defineProperty(exports, "__esModule", { value: true });
exports.renderSpriteBody = exports.renderSvgBody = exports.createColorChildrenResolver = exports.precomputeColorChildren = exports.computeViewBox = exports.renderSvgNode = void 0;
const tslib_1 = require("tslib");
const React = tslib_1.__importStar(require("react"));
/** The literal prefix of an SVG `url(#…)` local reference. */
const URL_REF = 'url(#';
/**
 * Recursively renders an {@link SvgNode} tree into React elements. When `prefix`
 * is set, every `id` definition and `url(#…)` reference is scoped with it so
 * repeated color icons don't collide in the global DOM id namespace.
 */
const renderSvgNode = (node, key, prefix) => {
    const [tag, attrs, ...children] = node;
    let scoped = attrs;
    if (prefix && attrs) {
        const next = {};
        for (const attrName in attrs) {
            const attrValue = attrs[attrName];
            if (typeof attrValue !== 'string') {
                next[attrName] = attrValue; // style object — passthrough
            }
            else if (attrName === 'id') {
                next[attrName] = prefix + attrValue; // scope the id definition
            }
            else {
                // Scope a `url(#…)` reference by splicing `prefix` in right after `url(#`.
                // A single indexOf locates it (-1 short-circuits with no allocation, e.g.
                // for long `d` paths); color-icon attributes carry at most one reference,
                // so no global replace is needed.
                const refAt = attrValue.indexOf(URL_REF);
                next[attrName] =
                    refAt < 0
                        ? attrValue
                        : attrValue.slice(0, refAt + URL_REF.length) + prefix + attrValue.slice(refAt + URL_REF.length);
            }
        }
        scoped = next;
    }
    return React.createElement(tag, { ...scoped, key }, ...children.map((child, idx) => exports.renderSvgNode(child, idx, prefix)));
};
exports.renderSvgNode = renderSvgNode;
/** Normalizes the icon width into a square viewBox dimension. */
const computeViewBox = (width) => (width === '1em' ? '20' : width);
exports.computeViewBox = computeViewBox;
/**
 * Pre-renders color SVG node trees so the recursion stays out of the hot render
 * path. Returns `undefined` for mono-color (path `string[]`) icons. When
 * `prefix` is set, locally-scoped `id`s (gradients, clip paths, filters) and
 * their `url(#…)` references are prefixed so repeated color icons don't collide
 * in the global DOM id namespace.
 */
const precomputeColorChildren = (pathsOrSvg, options, prefix) => typeof pathsOrSvg !== 'string' && ((options === null || options === void 0 ? void 0 : options.color) || Array.isArray(pathsOrSvg[0]))
    ? pathsOrSvg.map((node, i) => exports.renderSvgNode(node, i, prefix))
    : undefined;
exports.precomputeColorChildren = precomputeColorChildren;
/** Shared mono-color resolver — calls no hooks and allocates nothing per component. */
const noColorChildren = () => undefined;
/**
 * Creates a per-component resolver for a color icon's rendered children.
 *
 * Color detection and the shared (unscoped) precompute run once here, at factory
 * time. The returned hook:
 * - for mono-color icons, is a shared no-op returning `undefined` (no React hook,
 *   no per-component or per-instance allocation);
 * - for color icons, memoizes per instance on `idPrefix`, so re-renders reuse the
 *   element tree and only rebuild when the prefix actually changes.
 *
 * Branching here (an icon's "color-ness" is fixed at factory time) keeps hook
 * order stable per component, so only color icons ever pay for the memo hook.
 */
const createColorChildrenResolver = (pathsOrSvg, options) => {
    const colorChildren = exports.precomputeColorChildren(pathsOrSvg, options);
    if (!colorChildren) {
        return noColorChildren;
    }
    return function useColorChildren(idPrefix) {
        return React.useMemo(() => (idPrefix ? exports.precomputeColorChildren(pathsOrSvg, options, idPrefix) : colorChildren), [idPrefix]);
    };
};
exports.createColorChildrenResolver = createColorChildrenResolver;
/**
 * Renders the `<svg>` element body for an already-resolved element `state`.
 * Handles the three content forms:
 * - raw innerHTML `string` (deprecated, CSP-unsafe `dangerouslySetInnerHTML`),
 * - pre-rendered color children ({@link createColorChildrenResolver}),
 * - mono-color path `d` strings.
 *
 * `state` must already carry className/fill/width/height/viewBox/xmlns/ref.
 */
const renderSvgBody = (state, pathsOrSvg, colorChildren) => {
    if (typeof pathsOrSvg === 'string') {
        return React.createElement('svg', { ...state, dangerouslySetInnerHTML: { __html: pathsOrSvg } });
    }
    if (colorChildren) {
        return React.createElement('svg', state, ...colorChildren);
    }
    return React.createElement('svg', state, ...pathsOrSvg.map((d) => React.createElement('path', { d, fill: state.fill })));
};
exports.renderSvgBody = renderSvgBody;
/**
 * Renders the `<svg>` element body for a sprite icon — references an external
 * symbol via `<use href>`. `state` must already carry
 * className/width/height/viewBox/xmlns/ref.
 */
const renderSpriteBody = (state, iconId, spritePath) => {
    const href = spritePath ? `${spritePath}#${iconId}` : `#${iconId}`;
    return React.createElement('svg', state, React.createElement('use', { href }));
};
exports.renderSpriteBody = renderSpriteBody;
