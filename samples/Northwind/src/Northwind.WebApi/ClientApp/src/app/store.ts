import { configureStore } from "@reduxjs/toolkit";
import { setupListeners } from "@reduxjs/toolkit/query";

import { baseApi } from "@/lib/api";
import { persistStorefront, storefrontSlice } from "@/lib/store";

/** Uygulamanın tek Redux store'u: RTK Query önbelleği + mağaza/sepet durumu. */
export const store = configureStore({
  reducer: {
    [baseApi.reducerPath]: baseApi.reducer,
    storefront: storefrontSlice.reducer,
  },
  middleware: (getDefaultMiddleware) => getDefaultMiddleware().concat(baseApi.middleware),
});

// refetchOnFocus / refetchOnReconnect seçenekleri için.
setupListeners(store.dispatch);

// Sepet ve seçili mağaza tarayıcıda (localStorage) kalıcı.
let lastStorefront = store.getState().storefront;
store.subscribe(() => {
  const current = store.getState().storefront;
  if (current !== lastStorefront) {
    lastStorefront = current;
    persistStorefront(current);
  }
});

export type RootState = ReturnType<typeof store.getState>;
export type AppDispatch = typeof store.dispatch;
