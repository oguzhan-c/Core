import { baseApi, params } from "@/lib/api";
import type { Category, DynamicQuery, Paginate, Product, ProductListItem, SearchExamples, Shipper, Supplier } from "@/lib/types";

export interface ProductListArgs {
  index: number;
  size: number;
  search?: string;
  categoryId?: string;
  includeDiscontinued?: boolean;
}

export interface ProductDetails {
  name: string;
  categoryId: string | null;
  supplierId: string | null;
  quantityPerUnit: string | null;
  reorderLevel: number;
}

export interface CreateProductRequest extends ProductDetails {
  unitPrice: number;
  unitsInStock: number;
}

/** Ürün değişince: ürün listeleri, kategori ürün sayıları, mağaza kataloğu, değişiklik geçmişi, panel. */
const afterProductChange = ["Product", "Category", "StoreCatalog", "AuditLog", "Dashboard"] as const;

/** Yönetim paneli: kategori, ürün, tedarikçi, kargo firması ve dinamik arama. */
export const catalogApi = baseApi.injectEndpoints({
  endpoints: (build) => ({
    // ------------------------------------------------------------ kategoriler
    getCategories: build.query<Category[], void>({
      query: () => "/api/categories",
      providesTags: ["Category"],
    }),

    saveCategory: build.mutation<void, { id?: string; name: string; description: string | null }>({
      query: ({ id, ...body }) => (id ? { url: `/api/categories/${id}`, method: "PUT", body } : { url: "/api/categories", method: "POST", body }),
      invalidatesTags: ["Category", "StoreCatalog"],
    }),

    deleteCategory: build.mutation<void, string>({
      query: (id) => ({ url: `/api/categories/${id}`, method: "DELETE" }),
      invalidatesTags: ["Category", "StoreCatalog"],
    }),

    // ------------------------------------------------------------ ürünler
    getProducts: build.query<Paginate<ProductListItem>, ProductListArgs>({
      query: (args) => ({ url: "/api/products", params: params({ ...args }) }),
      providesTags: ["Product"],
    }),

    getProduct: build.query<Product, string>({
      query: (id) => `/api/products/${id}`,
      providesTags: ["Product"],
    }),

    getProductsToReorder: build.query<Product[], void>({
      query: () => "/api/products/to-reorder",
      providesTags: ["Product"],
    }),

    createProduct: build.mutation<{ id: string }, CreateProductRequest>({
      query: (body) => ({ url: "/api/products", method: "POST", body }),
      invalidatesTags: [...afterProductChange],
    }),

    updateProduct: build.mutation<void, { id: string } & ProductDetails>({
      query: ({ id, ...body }) => ({ url: `/api/products/${id}`, method: "PUT", body }),
      invalidatesTags: [...afterProductChange],
    }),

    changePrice: build.mutation<void, { id: string; unitPrice: number }>({
      query: ({ id, unitPrice }) => ({ url: `/api/products/${id}/price`, method: "PUT", body: { unitPrice } }),
      invalidatesTags: [...afterProductChange],
    }),

    restockProduct: build.mutation<void, { id: string; quantity: number }>({
      query: ({ id, quantity }) => ({ url: `/api/products/${id}/restock`, method: "POST", body: { quantity } }),
      invalidatesTags: [...afterProductChange],
    }),

    discontinueProduct: build.mutation<void, string>({
      query: (id) => ({ url: `/api/products/${id}/discontinue`, method: "POST" }),
      invalidatesTags: [...afterProductChange, "Outbox"],
    }),

    deleteProduct: build.mutation<void, string>({
      query: (id) => ({ url: `/api/products/${id}`, method: "DELETE" }),
      invalidatesTags: [...afterProductChange],
    }),

    // ------------------------------------------------------------ salt-okunur listeler
    getSuppliers: build.query<Paginate<Supplier>, { index?: number; size?: number; country?: string }>({
      query: (args) => ({ url: "/api/suppliers", params: params({ ...args }) }),
      providesTags: ["Supplier"],
    }),

    getShippers: build.query<Shipper[], void>({
      query: () => "/api/shippers",
      providesTags: ["Shipper"],
    }),

    // ------------------------------------------------------------ dinamik arama (filtre laboratuvarı)
    getSearchExamples: build.query<SearchExamples, void>({
      query: () => "/api/search/examples",
      keepUnusedDataFor: 3600,
    }),

    /** POST ile gönderilen ama veri değiştirmeyen arama: query olarak tanımlı (önbelleklenir, tekrar istenebilir). */
    search: build.query<Paginate<unknown>, { endpoint: string; body: DynamicQuery; index: number; size: number }>({
      query: ({ endpoint, body, index, size }) => ({ url: endpoint, method: "POST", body, params: { index, size } }),
      providesTags: ["Product", "Customer", "Order"],
    }),
  }),
});

export const {
  useGetCategoriesQuery,
  useSaveCategoryMutation,
  useDeleteCategoryMutation,
  useGetProductsQuery,
  useGetProductQuery,
  useGetProductsToReorderQuery,
  useCreateProductMutation,
  useUpdateProductMutation,
  useChangePriceMutation,
  useRestockProductMutation,
  useDiscontinueProductMutation,
  useDeleteProductMutation,
  useGetSuppliersQuery,
  useGetShippersQuery,
  useGetSearchExamplesQuery,
  useLazySearchQuery,
} = catalogApi;
