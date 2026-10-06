import { useState } from "react";
import { SearchIcon } from "lucide-react";

import { DataPagination } from "@/components/common/data-pagination";
import { PageHeader } from "@/components/common/page-header";
import { EmptyState, QueryState } from "@/components/common/query-state";
import { useDebounced } from "@/components/common/use-debounced";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { formatDate } from "@/lib/format";
import { useGetUsersQuery } from "@/services/admin";

const ALL = "all";

export function AdminUsersPage() {
  const [index, setIndex] = useState(0);
  const [search, setSearch] = useState("");
  const [role, setRole] = useState(ALL);
  const debounced = useDebounced(search);

  const users = useGetUsersQuery({ index, size: 20, search: debounced, role: role === ALL ? undefined : role });

  return (
    <>
      <PageHeader title="Kullanıcılar" description="Bu mağazanın personeli ve siteden kayıt olan müşteriler." />
      <div className="flex flex-col gap-3 sm:flex-row">
        <div className="relative flex-1">
          <SearchIcon className="text-muted-foreground absolute top-2.5 left-3 size-4" />
          <Input
            className="pl-9"
            placeholder="E-posta ya da ad…"
            value={search}
            onChange={(e) => {
              setSearch(e.target.value);
              setIndex(0);
            }}
          />
        </div>
        <Select
          value={role}
          onValueChange={(v) => {
            setRole(v);
            setIndex(0);
          }}
        >
          <SelectTrigger className="w-full sm:w-44">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value={ALL}>Tüm roller</SelectItem>
            {["Admin", "Sales", "Warehouse", "Customer"].map((r) => (
              <SelectItem key={r} value={r}>
                {r}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>
      <QueryState isLoading={users.isLoading} error={users.error} />
      {users.data?.count === 0 && <EmptyState title="Kullanıcı bulunamadı" />}
      {!!users.data?.count && (
        <Card className="py-2">
          <CardContent className="px-2">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Kullanıcı</TableHead>
                  <TableHead>Roller</TableHead>
                  <TableHead>E-posta</TableHead>
                  <TableHead>Kayıt</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {users.data.items.map((u) => (
                  <TableRow key={u.id}>
                    <TableCell>
                      <div className="font-medium">
                        {u.firstName} {u.lastName}
                      </div>
                      <div className="text-muted-foreground text-xs">{u.email}</div>
                    </TableCell>
                    <TableCell className="space-x-1">
                      {u.roles.map((r) => (
                        <Badge key={r} variant="outline">
                          {r}
                        </Badge>
                      ))}
                    </TableCell>
                    <TableCell>
                      {u.emailConfirmed ? <Badge variant="success">Doğrulandı</Badge> : <Badge variant="warning">Bekliyor</Badge>}
                      {u.lockoutEnd && new Date(u.lockoutEnd) > new Date() && (
                        <Badge variant="destructive" className="ml-1">
                          Kilitli
                        </Badge>
                      )}
                    </TableCell>
                    <TableCell>{formatDate(u.createdAt)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      <DataPagination page={users.data} onPageChange={setIndex} />
    </>
  );
}
