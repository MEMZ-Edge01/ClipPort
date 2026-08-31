import * as React from 'react';
/**
 * Structured SVG element tree used for color icons (CSP-safe alternative to
 * raw innerHTML strings).
 */
export declare type SvgNode = [
    tag: string,
    attrs: Record<string, string | Record<string, string>> | null,
    ...children: SvgNode[]
];
/**
 * Recursively renders an {@link SvgNode} tree into React elements. When `prefix`
 * is set, every `id` definition and `url(#…)` reference is scoped with it so
 * repeated color icons don't collide in the global DOM id namespace.
 */
export declare const renderSvgNode: (node: SvgNode, key: number, prefix?: string | undefined) => React.ReactElement;
/** Normalizes the icon width into a square viewBox dimension. */
export declare const computeViewBox: (width: string) => string;
/**
 * Pre-renders color SVG node trees so the recursion stays out of the hot render
 * path. Returns `undefined` for mono-color (path `string[]`) icons. When
 * `prefix` is set, locally-scoped `id`s (gradients, clip paths, filters) and
 * their `url(#…)` references are prefixed so repeated color icons don't collide
 * in the global DOM id namespace.
 */
export declare const precomputeColorChildren: (pathsOrSvg: string[] | string | SvgNode[], options?: {
    color?: boolean | undefined;
} | undefined, prefix?: string | undefined) => React.ReactElement[] | undefined;
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
export declare const createColorChildrenResolver: (pathsOrSvg: string[] | string | SvgNode[], options?: {
    color?: boolean | undefined;
} | undefined) => (idPrefix?: string | undefined) => React.ReactElement[] | undefined;
declare type RenderSvgState = {
    fill?: string;
};
/**
 * Renders the `<svg>` element body for an already-resolved element `state`.
 * Handles the three content forms:
 * - raw innerHTML `string` (deprecated, CSP-unsafe `dangerouslySetInnerHTML`),
 * - pre-rendered color children ({@link createColorChildrenResolver}),
 * - mono-color path `d` strings.
 *
 * `state` must already carry className/fill/width/height/viewBox/xmlns/ref.
 */
export declare const renderSvgBody: <TState extends RenderSvgState>(state: TState, pathsOrSvg: string[] | string | SvgNode[], colorChildren: React.ReactElement[] | undefined) => React.ReactElement;
/**
 * Renders the `<svg>` element body for a sprite icon — references an external
 * symbol via `<use href>`. `state` must already carry
 * className/width/height/viewBox/xmlns/ref.
 */
export declare const renderSpriteBody: <TState extends object>(state: TState, iconId: string, spritePath?: string | undefined) => React.ReactElement;
export {};
