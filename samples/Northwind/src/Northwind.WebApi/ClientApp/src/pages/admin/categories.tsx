import { useState } from "react";
import { PencilIcon, PlusIcon, Trash2Icon } from "lucide-react";
import { toast } from "sonner";

import { ConfirmDialog } from "@/components/common/confirm-dialog";
import { Field } from "@/components/common/field";
import { PageHeader } from "@/components/common/page-header";
import { QueryState } from "@/components/common/query-state";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog, DialogContent, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Textarea } from "@/components/ui/textarea";
import { errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import type { Category } from "@/lib/types";
import { useDeleteCategoryMutation, useGetCategoriesQuery, useSaveCategoryMutation } from "@/services/catalog";

export function AdminCategoriesPage() {
  const isAdmin = useAuth().hasRole("Admin");
  const [editing, setEditing] = useState<Category | "new" | null>(null);
  const [deleting, setDeleting] = useState<Category | null>(null);

  const categories = useGetCategoriesQuery();
  const [deleteCategory] = useDeleteCategoryMutation();

  async function remove(id: string) {
    try {
      await deleteCategory(id).unwrap();
      toast.success("Kategori silindi.");
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <>
      <PageHeader
        title="Kategoriler"
        description="Liste sunucuda önbelleğe alınır (HybridCache); ekleme/silme önbelleği temizler."
        actions={
          isAdmin && (
            <Button onClick={() => setEditing("new")}>
              <PlusIcon /> Yeni kategori
            </Button>
          )
        }
      />
      <QueryState isLoading={categories.isLoading} error={categories.error} />
      {categories.data && (
        <Card className="py-2">
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Ad</TableHead>
                  <TableHead>Açıklama</TableHead>
                  <TableHead className="text-right">Ürün</TableHead>
                  {isAdmin && <TableHead className="w-24" />}
                </TableRow>
              </TableHeader>
              <TableBody>
                {categories.data.map((c) => (
                  <TableRow key={c.id}>
                    <TableCell className="font-medium">{c.name}</TableCell>
                    <TableCell className="text-muted-foreground max-w-md truncate">{c.description}</TableCell>
                    <TableCell className="text-right">{c.productCount}</TableCell>
                    {isAdmin && (
                      <TableCell className="text-right">
                        <Button variant="ghost" size="icon-sm" onClick={() => setEditing(c)} aria-label="Düzenle">
                          <PencilIcon />
                        </Button>
                        <Button variant="ghost" size="icon-sm" onClick={() => setDeleting(c)} aria-label="Sil">
                          <Trash2Icon />
                        </Button>
                      </TableCell>
                    )}
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}

      {editing && <CategoryDialog category={editing === "new" ? null : editing} onClose={() => setEditing(null)} />}
      <ConfirmDialog
        open={!!deleting}
        onOpenChange={(open) => !open && setDeleting(null)}
        title={`'${deleting?.name}' silinsin mi?`}
        description="Ürünü olan kategori silinemez (iş kuralı)."
        confirmText="Sil"
        destructive
        onConfirm={() => (deleting ? remove(deleting.id) : undefined)}
      />
    </>
  );
}

function CategoryDialog({ category, onClose }: { category: Category | null; onClose: () => void }) {
  const [name, setName] = useState(category?.name ?? "");
  const [description, setDescription] = useState(category?.description ?? "");

  // Kayıt "Category" etiketini geçersiz kılar: liste kendiliğinden yenilenir.
  const [saveCategory, { isLoading: saving }] = useSaveCategoryMutation();

  async function save() {
    try {
      await saveCategory({ id: category?.id, name, description: description || null }).unwrap();
      toast.success("Kaydedildi.");
      onClose();
    } catch (e) {
      toast.error(errorMessage(e));
    }
  }

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{category ? "Kategoriyi düzenle" : "Yeni kategori"}</DialogTitle>
        </DialogHeader>
        <form
          className="grid gap-4"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <Field label="Ad" htmlFor="name">
            <Input id="name" required value={name} onChange={(e) => setName(e.target.value)} />
          </Field>
          <Field label="Açıklama" htmlFor="description">
            <Textarea id="description" value={description} onChange={(e) => setDescription(e.target.value)} />
          </Field>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Vazgeç
            </Button>
            <Button type="submit" disabled={saving}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
