import { useState } from "react";
import {
  ArchiveXIcon,
  DollarSignIcon,
  HistoryIcon,
  MoreHorizontalIcon,
  PackagePlusIcon,
  PencilIcon,
  PlusIcon,
  SearchIcon,
  Trash2Icon,
} from "lucide-react";
import { toast } from "sonner";

import { AuditHistory } from "@/components/common/audit-history";
import { ConfirmDialog } from "@/components/common/confirm-dialog";
import { DataPagination } from "@/components/common/data-pagination";
import { Field } from "@/components/common/field";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { useDebounced } from "@/components/common/use-debounced";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import { formatMoney } from "@/lib/format";
import type { Category, ProductListItem } from "@/lib/types";
import { cn } from "@/lib/utils";
import {
  useChangePriceMutation,
  useCreateProductMutation,
  useDeleteProductMutation,
  useDiscontinueProductMutation,
  useGetCategoriesQuery,
  useGetProductQuery,
  useGetProductsQuery,
  useGetSuppliersQuery,
  useRestockProductMutation,
  useUpdateProductMutation,
} from "@/services/catalog";

type DialogState =
  | { kind: "create" }
  | { kind: "edit" | "price" | "restock" | "discontinue" | "delete" | "history"; product: ProductListItem }
  | null;

const ALL = "all";

export function AdminProductsPage() {
  const { hasRole } = useAuth();
  const isAdmin = hasRole("Admin");
  const canRestock = hasRole("Warehouse", "Admin");

  const [index, setIndex] = useState(0);
  const [search, setSearch] = useState("");
  const [categoryId, setCategoryId] = useState(ALL);
  const [includeDiscontinued, setIncludeDiscontinued] = useState(false);
  const [dialog, setDialog] = useState<DialogState>(null);
  const debounced = useDebounced(search);

  const categories = useGetCategoriesQuery();
  const products = useGetProductsQuery({
    index,
    size: 15,
    search: debounced,
    categoryId: categoryId === ALL ? undefined : categoryId,
    includeDiscontinued,
  });

  // Ürün işlemleri ürün, kategori, katalog, panel ve geçmiş etiketlerini geçersiz kılar (services/catalog.ts):
  // ilgili listeler elle yenilemeye gerek kalmadan güncellenir.
  const [discontinueProduct] = useDiscontinueProductMutation();
  const [deleteProduct] = useDeleteProductMutation();

  async function discontinue(id: string) {
    try {
      await discontinueProduct(id).unwrap();
      toast.success("Ürün satıştan kaldırıldı. Bildirim outbox üzerinden gönderilecek.");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  async function remove(id: string) {
    try {
      await deleteProduct(id).unwrap();
      toast.success("Ürün silindi (soft delete).");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  const close = () => setDialog(null);

  return (
    <>
      <PageHeader
        title="Ürünler"
        description="Sunucu tarafında sayfalama, arama ve kategori filtresi. Gelişmiş filtre için Filtre laboratuvarı."
        actions={
          isAdmin && (
            <Button onClick={() => setDialog({ kind: "create" })}>
              <PlusIcon /> Yeni ürün
            </Button>
          )
        }
      />

      <div className="flex flex-col gap-3 sm:flex-row sm:items-center">
        <div className="relative flex-1">
          <SearchIcon className="text-muted-foreground absolute top-2.5 left-3 size-4" />
          <Input
            className="pl-9"
            placeholder="Ürün adı…"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setIndex(0);
            }}
          />
        </div>
        <Select
          value={categoryId}
          onValueChange={(v) => {
            setCategoryId(v);
            setIndex(0);
          }}
        >
          <SelectTrigger className="w-full sm:w-52">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Tüm kategoriler</SelectItem>
            {categories.data?.map((c) => (
              <SelectItem key={c.id} value={c.id}>
                {c.name}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <div className="flex items-center gap-2">
          <Checkbox
            id="discontinued"
            checked={includeDiscontinued}
            onCheckedChange={(v) => {
              setIncludeDiscontinued(v === true);
              setIndex(0);
            }}
          />
          <Label htmlFor="discontinued" className="whitespace-nowrap">
            Satıştan kalkanlar
          </Label>
        </div>
      </div>

      <QueryState isLoading={products.isLoading} error={products.error} />
      {products.data?.count === 0 && <EmptyState title="Ürün bulunamadı" />}

      {!!products.data?.count && (
        <Card className={cn("py-2", products.isFetching && "opacity-70")}>
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Ürün</TableHead>
                  <TableHead>Kategori</TableHead>
                  <TableHead>Tedarikçi</TableHead>
                  <TableHead className="text-right">Fiyat</TableHead>
                  <TableHead className="text-right">Stok</TableHead>
                  <TableHead>Durum</TableHead>
                  <TableHead className="w-10" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {products.data.items.map((p) => (
                  <TableRow key={p.id}>
                    <TableCell>
                      <div className="font-medium">{p.name}</div>
                      <div className="text-muted-foreground text-xs">{p.quantityPerUnit}</div>
                    </TableCell>
                    <TableCell>{p.categoryName ?? "—"}</TableCell>
                    <TableCell className="max-w-48 truncate">{p.supplierName ?? "—"}</TableCell>
                    <TableCell className="text-right">{formatMoney(p.unitPrice)}</TableCell>
                    <TableCell className="text-right">{p.unitsInStock}</TableCell>
                    <TableCell>
                      {p.isDiscontinued ? <Badge variant="secondary">Satışta değil</Badge> : <Badge variant="success">Satışta</Badge>}
                    </TableCell>
                    <TableCell>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon-sm" aria-label="İşlemler">
                            <MoreHorizontalIcon />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          {isAdmin && (
                            <DropdownMenuItem onSelect={() => setDialog({ kind: "edit", product: p })}>
                              <PencilIcon /> Düzenle
                            </DropdownMenuItem>
                          )}
                          {isAdmin && (
                            <DropdownMenuItem onSelect={() => setDialog({ kind: "price", product: p })}>
                              <DollarSignIcon /> Fiyat değiştir
                            </DropdownMenuItem>
                          )}
                          {canRestock && (
                            <DropdownMenuItem onSelect={() => setDialog({ kind: "restock", product: p })}>
                              <PackagePlusIcon /> Stok girişi
                            </DropdownMenuItem>
                          )}
                          {isAdmin && (
                            <DropdownMenuItem onSelect={() => setDialog({ kind: "history", product: p })}>
                              <HistoryIcon /> Değişiklik geçmişi
                            </DropdownMenuItem>
                          )}
                          {isAdmin && !p.isDiscontinued && (
                            <>
                              <DropdownMenuSeparator />
                              <DropdownMenuItem onSelect={() => setDialog({ kind: "discontinue", product: p })}>
                                <ArchiveXIcon /> Satıştan kaldır
                              </DropdownMenuItem>
                            </>
                          )}
                          {isAdmin && (
                            <DropdownMenuItem variant="destructive" onSelect={() => setDialog({ kind: "delete", product: p })}>
                              <Trash2Icon /> Sil
                            </DropdownMenuItem>
                          )}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}

      <DataPagination page={products.data} onPageChange={setIndex} />

      {(dialog?.kind === "create" || dialog?.kind === "edit") && (
        <ProductFormDialog productId={dialog.kind === "edit" ? dialog.product.id : undefined} categories={categories.data ?? []} onClose={close} />
      )}
      {dialog?.kind === "price" && <PriceDialog product={dialog.product} onClose={close} />}
      {dialog?.kind === "restock" && <RestockDialog product={dialog.product} onClose={close} />}
      {dialog?.kind === "history" && (
        <Dialog open onOpenChange={(open) => !open && close()}>
          <DialogContent className="sm:max-w-2xl">
            <DialogHeader>
              <DialogTitle>{dialog.product.name}</DialogTitle>
              <DialogDescription>Fiyat, stok ve diğer alanların değişiklik geçmişi.</DialogDescription>
            </DialogHeader>
            <AuditHistory entityType="Product" entityId={dialog.product.id} />
          </DialogContent>
        </Dialog>
      )}
      <ConfirmDialog
        open={dialog?.kind === "discontinue"}
        onOpenChange={(open) => !open && close()}
        title="Ürün satıştan kaldırılsın mı?"
        description="Ürün yeni siparişlere eklenemez. ProductDiscontinued event'i outbox'a yazılır ve arka planda yayınlanır."
        confirmText="Satıştan kaldır"
        onConfirm={() => (dialog?.kind === "discontinue" ? discontinue(dialog.product.id) : undefined)}
      />
      <ConfirmDialog
        open={dialog?.kind === "delete"}
        onOpenChange={(open) => !open && close()}
        title="Ürün silinsin mi?"
        description="Soft delete: kayıt veritabanında kalır, listelerde görünmez."
        confirmText="Sil"
        destructive
        onConfirm={() => (dialog?.kind === "delete" ? remove(dialog.product.id) : undefined)}
      />
    </>
  );
}

function ProductFormDialog({
  productId,
  categories,
  onClose,
}: {
  productId?: string;
  categories: Category[];
  onClose: () => void;
}) {
  const suppliers = useGetSuppliersQuery({ size: 100 });
  // Yeni üründe istek atılmaz (skip).
  const existing = useGetProductQuery(productId ?? "", { skip: !productId });
  const [createProduct, { isLoading: creating }] = useCreateProductMutation();
  const [updateProduct, { isLoading: updating }] = useUpdateProductMutation();

  const loaded = existing.data;
  const [form, setForm] = useState<{
    name?: string;
    categoryId?: string;
    supplierId?: string;
    quantityPerUnit?: string;
    unitPrice?: string;
    unitsInStock?: string;
    reorderLevel?: string;
  }>({});

  // Düzenlemede alanlar yüklenen üründen başlar; kullanıcı değiştirdikçe form'a yazılır.
  const value = {
    name: form.name ?? loaded?.name ?? "",
    categoryId: form.categoryId ?? loaded?.categoryId ?? ALL,
    supplierId: form.supplierId ?? loaded?.supplierId ?? ALL,
    quantityPerUnit: form.quantityPerUnit ?? loaded?.quantityPerUnit ?? "",
    unitPrice: form.unitPrice ?? "0",
    unitsInStock: form.unitsInStock ?? "0",
    reorderLevel: form.reorderLevel ?? String(loaded?.reorderLevel ?? 10),
  };

  async function save() {
    const details = {
      name: value.name,
      categoryId: value.categoryId === ALL ? null : value.categoryId,
      supplierId: value.supplierId === ALL ? null : value.supplierId,
      quantityPerUnit: value.quantityPerUnit || null,
      reorderLevel: Number(value.reorderLevel),
    };

    try {
      if (productId) await updateProduct({ id: productId, ...details }).unwrap();
      else await createProduct({ ...details, unitPrice: Number(value.unitPrice), unitsInStock: Number(value.unitsInStock) }).unwrap();

      toast.success(productId ? "Ürün güncellendi." : "Ürün eklendi.");
      onClose();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{productId ? "Ürünü düzenle" : "Yeni ürün"}</DialogTitle>
          {productId && <DialogDescription>Fiyat ve stok ayrı işlemlerle değişir (değişiklik geçmişinde görünür).</DialogDescription>}
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Field label="Ad" htmlFor="name">
            <Input id="name" required value={value.name} onChange={(e) => setForm({ ...form, name: e.target.value })} />
          </Field>
          <div className="grid grid-cols-2 gap-3">
            <Field label="Kategori">
              <Select value={value.categoryId} onValueChange={(v) => setForm({ ...form, categoryId: v })}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>Kategorisiz</SelectItem>
                  {categories.map((c) => (
                    <SelectItem key={c.id} value={c.id}>
                      {c.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field label="Tedarikçi">
              <Select value={value.supplierId} onValueChange={(v) => setForm({ ...form, supplierId: v })}>
                <SelectTrigger className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={ALL}>Yok</SelectItem>
                  {suppliers.data?.items.map((s) => (
                    <SelectItem key={s.id} value={s.id}>
                      {s.companyName}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
          </div>
          <Field label="Satış birimi" htmlFor="qpu" hint='Ör. "24 - 12 oz bottles"'>
            <Input id="qpu" value={value.quantityPerUnit} onChange={(e) => setForm({ ...form, quantityPerUnit: e.target.value })} />
          </Field>
          <div className="grid grid-cols-3 gap-3">
            {!productId && (
              <>
                <Field label="Fiyat" htmlFor="price">
                  <Input id="price" type="number" min="0" step="0.01" value={value.unitPrice} onChange={(e) => setForm({ ...form, unitPrice: e.target.value })} />
                </Field>
                <Field label="Stok" htmlFor="stock">
                  <Input id="stock" type="number" min="0" value={value.unitsInStock} onChange={(e) => setForm({ ...form, unitsInStock: e.target.value })} />
                </Field>
              </>
            )}
            <Field label="Sipariş seviyesi" htmlFor="reorder">
              <Input id="reorder" type="number" min="0" value={value.reorderLevel} onChange={(e) => setForm({ ...form, reorderLevel: e.target.value })} />
            </Field>
          </div>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Vazgeç
            </Button>
            <Button type="submit" disabled={creating || updating || (!!productId && !loaded)}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

function PriceDialog({ product, onClose }: { product: ProductListItem; onClose: () => void }) {
  const [price, setPrice] = useState(String(product.unitPrice));
  const [changePrice, { isLoading: saving }] = useChangePriceMutation();

  async function save() {
    try {
      await changePrice({ id: product.id, unitPrice: Number(price) }).unwrap();
      toast.success("Fiyat güncellendi; eski ve yeni fiyat değişiklik geçmişine yazıldı.");
      onClose();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-sm">
        <DialogHeader>
          <DialogTitle>Fiyat değiştir</DialogTitle>
          <DialogDescription>
            {product.name} · şu an {formatMoney(product.unitPrice)}
          </DialogDescription>
        </DialogHeader>
        <Field label="Yeni fiyat" htmlFor="newPrice">
          <Input id="newPrice" type="number" min="0" step="0.01" value={price} onChange={(e) => setPrice(e.target.value)} />
        </Field>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Vazgeç
          </Button>
          <Button onClick={save} disabled={saving}>
            Kaydet
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

function RestockDialog({ product, onClose }: { product: ProductListItem; onClose: () => void }) {
  const [quantity, setQuantity] = useState("10");
  const [restock, { isLoading: saving }] = useRestockProductMutation();

  async function save() {
    try {
      await restock({ id: product.id, quantity: Number(quantity) }).unwrap();
      toast.success("Stok girişi yapıldı.");
      onClose();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent className="sm:max-w-sm">
        <DialogHeader>
          <DialogTitle>Stok girişi</DialogTitle>
          <DialogDescription>
            {product.name} · şu an {product.unitsInStock} adet
          </DialogDescription>
        </DialogHeader>
        <Field label="Gelen miktar" htmlFor="qty">
          <Input id="qty" type="number" min="1" value={quantity} onChange={(e) => setQuantity(e.target.value)} />
        </Field>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Vazgeç
          </Button>
          <Button onClick={save} disabled={saving}>
            Ekle
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
