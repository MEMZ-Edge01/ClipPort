import * as React from 'react';
/**
 * Applies the inline styles shared by both font-icon factories:
 * - maps `primaryFill` -> `color` (unless `currentColor`)
 * - applies an explicit `fontSize`
 *
 * Mutates `state.style` in place to match the existing factory behaviour.
 */
export declare const applyFontStyle: (state: {
    style?: React.CSSProperties;
}, primaryFill: string | undefined, fontSize: number | string | undefined) => void;
/**
 * Renders the `<i>` element body for a font icon. `state` must already carry
 * className/style/a11y; the glyph `codepoint` is the element's text content.
 */
export declare const renderFontBody: <TState extends object>(state: TState, codepoint: string) => React.ReactElement;
