'use client';
"use strict";
Object.defineProperty(exports, "__esModule", {
    value: true
});
function _export(target, all) {
    for(var name in all)Object.defineProperty(target, name, {
        enumerable: true,
        get: Object.getOwnPropertyDescriptor(all, name).get
    });
}
_export(exports, {
    get RendererProvider () {
        return RendererProvider;
    },
    get useRenderer () {
        return useRenderer;
    }
});
const _jsxruntime = require("react/jsx-runtime");
const _core = require("@griffel/core");
const _react = require("react");
const _canUseDOM = require("./utils/canUseDOM.cjs");
/**
 * @private
 */ const RendererContext = /*#__PURE__*/ (0, _react.createContext)(/*#__PURE__*/ (0, _core.createDOMRenderer)());
const RendererProvider = ({ children, renderer, targetDocument })=>{
    // "rehydrateCache()" can't be called in effects as it needs to be called before any component will be rendered to
    // avoid double insertion of classes — useMemo runs synchronously before render, useEffect would be too late.
    // eslint-disable-next-line react-x/use-memo, react-hooks/void-use-memo
    (0, _react.useMemo)(()=>{
        if ((0, _canUseDOM.canUseDOM)()) {
            (0, _core.rehydrateRendererCache)(renderer, targetDocument);
        }
    }, [
        renderer,
        targetDocument
    ]);
    return (0, _jsxruntime.jsx)(RendererContext.Provider, {
        value: renderer,
        children: children
    });
};
function useRenderer() {
    return (0, _react.useContext)(RendererContext);
} //# sourceMappingURL=RendererContext.js.map
