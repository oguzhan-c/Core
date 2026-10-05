import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
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
import { api, errorMessage } from "@/lib/api";
import { useAuth } from "@/lib/auth";
import type { Category } from "@/lib/types";

export function AdminCategoriesPage() {
  const isAdmin = useAuth().hasRole("Admin");
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState<Category | "new" | null>(null);
  const [deleting, setDeleting] = useState<Category | null>(null);

  const categories = useQuery({ queryKey: ["categories"], queryFn: () => api<Category[]>("/api/categories") });

  const remove = useMutation({
    mutationFn: (id: string) => api(`/api/categories/${id}`, { method: "DELETE" }),
    onSuccess: () => {
      toast.success("Kategori silindi.");
      queryClient.invalidateQueries({ queryKey: ["categories"] });
    },
    onError: (e) => toast.error(errorMessage(e)),
  });

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
        onConfirm={() => (deleting ? remove.mutateAsync(deleting.id) : undefined)}
      />
    </>
  );
}

function CategoryDialog({ category, onClose }: { category: Category | null; onClose: () => void }) {
  const queryClient = useQueryClient();
  const [name, setName] = useState(category?.name ?? "");
  const [description, setDescription] = useState(category?.description ?? "");

  const save = useMutation({
    mutationFn: () =>
      category
        ? api(`/api/categories/${category.id}`, { method: "PUT", body: { name, description } })
        : api("/api/categories", { method: "POST", body: { name, description } }),
    onSuccess: () => {
      toast.success("Kaydedildi.");
      queryClient.invalidateQueries({ queryKey: ["categories"] });
      onClose();
    },
    onError: (e) => toast.error(errorMessage(e)),
  });

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
            save.mutate();
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
            <Button type="submit" disabled={save.isPending}>
              Kaydet
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}
