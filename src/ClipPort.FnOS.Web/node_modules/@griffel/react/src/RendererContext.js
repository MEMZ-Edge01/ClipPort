'use client';
import { jsx as _jsx } from "react/jsx-runtime";
import { createDOMRenderer, rehydrateRendererCache } from '@griffel/core';
import { createContext, useContext, useMemo } from 'react';
import { canUseDOM } from './utils/canUseDOM.js';
/**
 * @private
 */
const RendererContext = /*#__PURE__*/ createContext(/*#__PURE__*/ createDOMRenderer());
/**
 * @public
 */
export const RendererProvider = ({ children, renderer, targetDocument }) => {
    // "rehydrateCache()" can't be called in effects as it needs to be called before any component will be rendered to
    // avoid double insertion of classes — useMemo runs synchronously before render, useEffect would be too late.
    // eslint-disable-next-line react-x/use-memo, react-hooks/void-use-memo
    useMemo(() => {
        if (canUseDOM()) {
            rehydrateRendererCache(renderer, targetDocument);
        }
    }, [renderer, targetDocument]);
    return _jsx(RendererContext.Provider, { value: renderer, children: children });
};
/**
 * Returns an instance of current makeStyles() renderer.
 *
 * @private Exported as "useRenderer_unstable" use it on own risk. Can be changed or removed without a notice.
 */
export function useRenderer() {
    return useContext(RendererContext);
}
//# sourceMappingURL=RendererContext.js.map